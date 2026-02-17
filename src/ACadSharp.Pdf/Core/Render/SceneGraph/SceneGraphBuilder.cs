using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Pdf.Core.Render.Style;
using ACadSharp.Pdf.Core.Render.Text;
using ACadSharp.Pdf.Core.Render.Transforms;
using ACadSharp.Tables;
using CSMath;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ACadSharp.Pdf.Core.Render.SceneGraph
{
	internal sealed class SceneGraphBuilder
	{
		private readonly Layout _layout;
		private readonly PdfConfiguration _configuration;
		private readonly PropertyResolver _resolver;
		private readonly RenderLog _log;
		private readonly BlockExpander _blockExpander;
		private readonly TextLayoutEngine _textLayout;

		private readonly struct InsertRenderContext
		{
			public ACadSharp.Color ByBlockColor { get; }
			public LineWeightType ByBlockLineWeight { get; }
			public LineType ByBlockLineType { get; }
			public Layer InsertLayer { get; }

			public InsertRenderContext(ACadSharp.Color byBlockColor, LineWeightType byBlockLineWeight, LineType byBlockLineType, Layer insertLayer)
			{
				this.ByBlockColor = byBlockColor;
				this.ByBlockLineWeight = byBlockLineWeight;
				this.ByBlockLineType = byBlockLineType;
				this.InsertLayer = insertLayer;
			}
		}

		public SceneGraphBuilder(Layout layout, PdfConfiguration configuration, PropertyResolver resolver, RenderLog log)
		{
			this._layout = layout ?? throw new ArgumentNullException(nameof(layout));
			this._configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
				this._resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
				this._log = log ?? throw new ArgumentNullException(nameof(log));
				this._blockExpander = new BlockExpander(log);
				this._textLayout = new TextLayoutEngine(layout, configuration, log);
			}

		public IReadOnlyList<RenderNode> Build(IReadOnlyList<Viewport> viewports, IReadOnlyList<Entity> paperEntities)
		{
			var nodes = new List<RenderNode>();

			if (viewports != null)
			{
				foreach (var vp in viewports)
				{
					var n = buildViewport(vp);
					if (n != null)
					{
						nodes.Add(n);
					}
				}
			}

			if (paperEntities != null)
			{
				foreach (var e in paperEntities)
				{
					var en = buildEntityNode(
						e,
						viewport: null,
						styleScaleToPaper: 1.0,
						textScaleToPaper: 1.0,
						containingInsert: null,
						parentTransform: Matrix4.Identity,
						depth: 0,
						activeBlocks: null);
					if (en != null)
					{
						nodes.Add(en);
					}
				}
			}

			return nodes;
		}

		private RenderNode buildViewport(Viewport viewport)
		{
			if (viewport == null)
			{
				return null;
			}

			// Clip rectangle in paper space
			BoundingBox box = viewport.GetBoundingBox();
			var clipPath = rectanglePath(viewport.Handle, (XY)box.Min, (XY)box.Max);

			Matrix4 modelToPaper = TransformHelper.ViewportModelToPaper(viewport);

			if (viewport.TwistAngle != 0)
			{
				this._log.Add(viewport.Handle, viewport.SubclassMarker, RenderStatus.Rendered, "Viewport twist angle ignored in Stage 00.");
			}

			if (viewport.ViewDirection != XYZ.AxisZ && viewport.ViewDirection != XYZ.Zero)
			{
				this._log.Add(viewport.Handle, viewport.SubclassMarker, RenderStatus.Rendered, "Viewport view direction not supported (orthographic top-view only); rendering as if top-view.");
			}

			var children = new List<RenderNode>();
			List<Entity> modelEntities;
			try
			{
				modelEntities = viewport.SelectEntities();
			}
			catch (Exception ex)
			{
				this._log.Add(viewport.Handle, viewport.SubclassMarker, RenderStatus.Error, $"Viewport entity selection failed: {ex.Message}");
				return null;
			}

			foreach (var e in modelEntities)
			{
				var child = buildEntityNode(
					e,
					viewport,
					styleScaleToPaper: viewport.ScaleFactor,
					textScaleToPaper: viewport.ScaleFactor,
					containingInsert: null,
					parentTransform: Matrix4.Identity,
					depth: 0,
					activeBlocks: null);
				if (child != null)
				{
					children.Add(child);
				}
			}

			var group = new GroupNode(viewport.Handle, modelToPaper, children);
			return new ClipNode(viewport.Handle, clipPath, new[] { group });
		}

			private RenderNode buildEntityNode(
				Entity entity,
				Viewport viewport,
				double styleScaleToPaper,
				double textScaleToPaper,
				InsertRenderContext? containingInsert,
				Matrix4 parentTransform,
				int depth,
				HashSet<string> activeBlocks)
			{
			if (entity == null)
			{
				return null;
			}

			HashSet<string> stack = activeBlocks ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			try
			{
					Entity prepared = applyLayerZeroInheritance(entity, containingInsert);

					if (prepared is Insert insert)
					{
						Layer effectiveInsertLayer = resolveEffectiveLayer(insert.Layer, containingInsert);
						var visInsert = getVisibilityWithLayer(insert, effectiveInsertLayer, viewport);
						if (visInsert != VisibilityDecision.Visible)
						{
							this._log.Add(insert.Handle, insert.SubclassMarker, RenderStatus.Skipped, $"Visibility gate: {visInsert}");
							return null;
						}

						return buildInsert(insert, effectiveInsertLayer, viewport, styleScaleToPaper, textScaleToPaper, containingInsert, parentTransform, depth, stack);
					}

				if (!isIdentity(parentTransform))
				{
					prepared = cloneWithTransform(prepared, parentTransform);
				}

				var vis = this._resolver.GetVisibility(prepared, viewport);
				if (vis != VisibilityDecision.Visible)
				{
					this._log.Add(prepared.Handle, prepared.SubclassMarker, RenderStatus.Skipped, $"Visibility gate: {vis}");
					return null;
				}

					switch (prepared)
					{
						case Line line:
							return buildLine(line, styleScaleToPaper, containingInsert);
						case Arc arc:
							return buildArc(arc, styleScaleToPaper, containingInsert);
						case Circle circle:
							return buildCircle(circle, styleScaleToPaper, containingInsert);
						case Ellipse ellipse:
							return buildEllipse(ellipse, styleScaleToPaper, containingInsert);
						case Point point:
							// Points are a constant paper-size dot; only viewport scaling should affect compensation.
							return buildPoint(point, textScaleToPaper, containingInsert);
						case IPolyline polyline:
							return buildPolyline(polyline, styleScaleToPaper, containingInsert);
						case TextEntity text:
							return buildText(text, textScaleToPaper, containingInsert);
						case MText mtext:
							return buildMText(mtext, textScaleToPaper, containingInsert);
						case Dimension dimension:
							return buildDimension(dimension, viewport, styleScaleToPaper, textScaleToPaper, containingInsert, parentTransform, depth, stack);
					default:
						this._log.Add(prepared.Handle, prepared.SubclassMarker, RenderStatus.NotImplemented, "Entity not supported in Stage 00 frontend.");
						this._configuration.Notify($"[{prepared.SubclassMarker}] Drawing not implemented (scene graph pipeline).", NotificationType.NotImplemented);
						return null;
				}
			}
			catch (Exception ex)
			{
				this._log.Add(entity.Handle, entity.SubclassMarker, RenderStatus.Error, ex.Message);
				this._configuration.Notify($"[{entity.SubclassMarker}] Scene-graph render failed: {ex.Message}", NotificationType.Warning, ex);
				return null;
			}
		}

			private RenderNode buildInsert(
				Insert insert,
				Layer effectiveInsertLayer,
				Viewport viewport,
				double styleScaleToPaper,
				double textScaleToPaper,
				InsertRenderContext? containingInsert,
				Matrix4 parentTransform,
				int depth,
				HashSet<string> activeBlocks)
			{
				if (!this._blockExpander.TryEnter(insert, depth, activeBlocks, out BlockRecord block, out string blockKey))
				{
					return null;
				}

			try
			{
				IReadOnlyList<Matrix4> cellTransforms = this._blockExpander.ComputeCellTransforms(insert, parentTransform);
					if (cellTransforms.Count == 0)
					{
						return null;
					}

					double childStyleScale = styleScaleToPaper * this._blockExpander.ComputeInsertScaleFactor(insert);
					InsertRenderContext childContext = createInsertContext(insert, effectiveInsertLayer, containingInsert);
					var nodes = new List<RenderNode>();

				foreach (var cellTransform in cellTransforms)
				{
					foreach (Entity blockEntity in block.Entities)
					{
						if (blockEntity is AttributeDefinition)
						{
							continue;
						}

						var node = buildEntityNode(
							blockEntity,
							viewport,
							childStyleScale,
							textScaleToPaper,
							childContext,
							cellTransform,
							depth + 1,
							activeBlocks);
						if (node != null)
						{
							nodes.Add(node);
						}
					}

					foreach (var attNode in buildInsertAttributes(insert, block, viewport, childStyleScale, textScaleToPaper, childContext, cellTransform, depth + 1, activeBlocks))
					{
						nodes.Add(attNode);
					}
				}

				if (nodes.Count == 0)
				{
					this._log.Add(insert.Handle, insert.SubclassMarker, RenderStatus.Skipped, $"Expanded block '{block.Name}' has no visible entities.");
					return null;
				}

				this._log.Add(insert.Handle, insert.SubclassMarker, RenderStatus.Rendered, $"Expanded block '{block.Name}' ({nodes.Count} node(s)).");
				return new GroupNode(insert.Handle, Matrix4.Identity, nodes);
			}
			finally
			{
				this._blockExpander.Leave(blockKey, activeBlocks);
			}
		}

		private IReadOnlyList<RenderNode> buildInsertAttributes(
			Insert insert,
			BlockRecord block,
			Viewport viewport,
			double styleScaleToPaper,
			double textScaleToPaper,
			InsertRenderContext childContext,
			Matrix4 cellTransform,
			int depth,
			HashSet<string> activeBlocks)
		{
			if (insert.Attributes == null || insert.Attributes.Count == 0)
			{
				return Array.Empty<RenderNode>();
			}

			var nodes = new List<RenderNode>();
			foreach (AttributeEntity att in insert.Attributes)
			{
				if (att == null)
				{
					continue;
				}

				if (att.IsInvisible || att.Flags.HasFlag(AttributeFlags.Hidden))
				{
					this._log.Add(att.Handle, att.SubclassMarker, RenderStatus.Skipped, "ATTRIB hidden/invisible.");
					continue;
				}

				AttributeEntity renderAtt = att.CloneTyped();
				AttributeDefinition def = findAttributeDefinition(block, att.Tag);
				if (string.IsNullOrEmpty(renderAtt.Value) && def != null)
				{
					renderAtt.Value = def.Value ?? string.Empty;
				}

					if (string.IsNullOrEmpty(renderAtt.Value))
					{
						if (renderAtt.MText == null)
						{
							this._log.Add(renderAtt.Handle, renderAtt.SubclassMarker, RenderStatus.Skipped, "ATTRIB value is empty.");
							continue;
						}
					}

					Entity renderEntity = renderAtt;
					if (renderAtt.MText != null)
					{
						MText mtext = this._textLayout.BuildAttributeMText(renderAtt);
						if (mtext != null)
						{
							renderEntity = mtext;
						}
					}

					var node = buildEntityNode(
						renderEntity,
						viewport,
						styleScaleToPaper,
						textScaleToPaper,
						childContext,
						cellTransform,
						depth,
						activeBlocks);
				if (node != null)
				{
					nodes.Add(node);
				}
			}

			return nodes;
		}

		private static AttributeDefinition findAttributeDefinition(BlockRecord block, string tag)
		{
			if (block == null || string.IsNullOrWhiteSpace(tag))
			{
				return null;
			}

			foreach (AttributeDefinition def in block.AttributeDefinitions)
			{
				if (string.Equals(def.Tag, tag, StringComparison.OrdinalIgnoreCase))
				{
					return def;
				}
			}

			return null;
		}

			private InsertRenderContext createInsertContext(Insert insert, Layer effectiveLayer, InsertRenderContext? parentContext)
			{
				ACadSharp.Color resolvedColor = resolveColorForInsertContext(insert, effectiveLayer, parentContext);
				LineWeightType resolvedLw = resolveLineWeightForInsertContext(insert, effectiveLayer, parentContext);
				LineType resolvedLt = resolveLineTypeForInsertContext(insert, effectiveLayer, parentContext);

				return new InsertRenderContext(resolvedColor, resolvedLw, resolvedLt, effectiveLayer);
			}

			private static Entity applyLayerZeroInheritance(Entity entity, InsertRenderContext? context)
			{
				if (!context.HasValue)
				{
					return entity;
			}

			if (context.Value.InsertLayer == null || entity.Layer == null)
			{
				return entity;
			}

			if (!isLayerZero(entity.Layer.Name))
			{
				return entity;
			}

			// Cloning INSERT can recursively clone its owned block graph; keep INSERT untouched here.
			if (entity is Insert)
			{
				return entity;
			}

			Entity clone = entity.CloneTyped();
			clone.Layer = context.Value.InsertLayer;
			return clone;
		}

		private static Entity cloneWithTransform(Entity entity, Matrix4 transform)
		{
			Entity clone = entity.CloneTyped();
			clone.ApplyTransform(new Transform(transform));
			return clone;
		}

			private StrokeStyle resolveStroke(Entity entity, double styleScaleToPaper, InsertRenderContext? containingInsert)
			{
				ACadSharp.Color? byBlockColor = containingInsert?.ByBlockColor;
				LineWeightType? byBlockLineWeight = containingInsert?.ByBlockLineWeight;
				LineType byBlockLineType = containingInsert?.ByBlockLineType;
				return this._resolver.ResolveStroke(entity, this._layout, styleScaleToPaper, byBlockColor, byBlockLineWeight, byBlockLineType);
			}

			private static Layer resolveEffectiveLayer(Layer entityLayer, InsertRenderContext? context)
			{
				if (entityLayer == null || !context.HasValue)
				{
					return entityLayer;
				}

				if (!isLayerZero(entityLayer.Name))
				{
					return entityLayer;
				}

				return context.Value.InsertLayer ?? entityLayer;
			}

			private VisibilityDecision getVisibilityWithLayer(Entity entity, Layer effectiveLayer, Viewport viewport)
			{
				if (entity == null) throw new ArgumentNullException(nameof(entity));

				if (entity.IsInvisible)
				{
					return VisibilityDecision.InvisibleFlag;
				}

				Layer layer = effectiveLayer ?? entity.Layer;
				if (layer != null)
				{
					if (!layer.IsOn)
					{
						return VisibilityDecision.LayerOff;
					}

					if (layer.Flags.HasFlag(LayerFlags.Frozen))
					{
						return VisibilityDecision.LayerFrozen;
					}

					if (!layer.PlotFlag)
					{
						return VisibilityDecision.LayerNotPlottable;
					}

					if (viewport != null && viewport.FrozenLayers != null && viewport.FrozenLayers.Count > 0)
					{
						if (viewport.FrozenLayers.Any(l => string.Equals(l?.Name, layer.Name, StringComparison.OrdinalIgnoreCase)))
						{
							return VisibilityDecision.ViewportFrozenLayer;
						}
					}
				}

				return VisibilityDecision.Visible;
			}

			private static ACadSharp.Color resolveColorForInsertContext(Insert insert, Layer effectiveLayer, InsertRenderContext? parentContext)
			{
				ACadSharp.Color color = insert.Color;

				if (color.IsTrueColor)
				{
					return mapAci7(color);
				}

				if (!color.IsByLayer && !color.IsByBlock && color.Index > 0)
				{
					return mapAci7(color);
				}

				if (color.IsByLayer)
				{
					return mapAci7(effectiveLayer?.Color ?? ACadSharp.Color.Default);
				}

				// ByBlock
				if (parentContext.HasValue)
				{
					return mapAci7(parentContext.Value.ByBlockColor);
				}

				return mapAci7(effectiveLayer?.Color ?? ACadSharp.Color.Default);
			}

			private static LineWeightType resolveLineWeightForInsertContext(Insert insert, Layer effectiveLayer, InsertRenderContext? parentContext)
			{
				LineWeightType lw = insert.LineWeight;
				switch (lw)
				{
					case LineWeightType.ByLayer:
						return effectiveLayer?.LineWeight ?? LineWeightType.Default;
					case LineWeightType.ByBlock:
						{
							LineWeightType resolved = parentContext?.ByBlockLineWeight
								?? (insert.Owner is BlockRecord record ? record.BlockEntity.LineWeight : LineWeightType.Default);
							if (resolved == LineWeightType.ByBlock)
							{
								resolved = LineWeightType.Default;
							}
							return resolved;
						}
					case LineWeightType.ByDIPs:
					case LineWeightType.Default:
						return LineWeightType.Default;
					default:
						return lw;
				}
			}

			private static LineType resolveLineTypeForInsertContext(Insert insert, Layer effectiveLayer, InsertRenderContext? parentContext)
			{
				LineType lt = insert.LineType ?? LineType.Continuous;

				if (string.Equals(lt.Name, LineType.ByLayerName, StringComparison.InvariantCultureIgnoreCase))
				{
					return effectiveLayer?.LineType ?? LineType.Continuous;
				}

				if (string.Equals(lt.Name, LineType.ByBlockName, StringComparison.InvariantCultureIgnoreCase))
				{
					return parentContext?.ByBlockLineType ?? LineType.Continuous;
				}

				return lt;
			}

			private static ACadSharp.Color mapAci7(ACadSharp.Color color)
			{
				if (!color.IsTrueColor && color.Index == 7)
				{
					return new ACadSharp.Color(0, 0, 0);
				}

				return color;
			}

			private static bool isLayerZero(string name)
			{
				return string.Equals(name, Layer.DefaultName, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(name, "0", StringComparison.OrdinalIgnoreCase);
		}

		private static bool isIdentity(Matrix4 matrix)
		{
			const double eps = 1e-12;
			return
				Math.Abs(matrix.M00 - 1.0) < eps &&
				Math.Abs(matrix.M11 - 1.0) < eps &&
				Math.Abs(matrix.M22 - 1.0) < eps &&
				Math.Abs(matrix.M33 - 1.0) < eps &&
				Math.Abs(matrix.M01) < eps &&
				Math.Abs(matrix.M02) < eps &&
				Math.Abs(matrix.M03) < eps &&
				Math.Abs(matrix.M10) < eps &&
				Math.Abs(matrix.M12) < eps &&
				Math.Abs(matrix.M13) < eps &&
				Math.Abs(matrix.M20) < eps &&
				Math.Abs(matrix.M21) < eps &&
				Math.Abs(matrix.M23) < eps &&
				Math.Abs(matrix.M30) < eps &&
				Math.Abs(matrix.M31) < eps &&
				Math.Abs(matrix.M32) < eps;
		}

			private PathNode buildLine(Line line, double styleScaleToPaper, InsertRenderContext? containingInsert)
			{
				var stroke = resolveStroke(line, styleScaleToPaper, containingInsert);
				var segs = new PathSegment[]
				{
				new MoveTo((XY)line.StartPoint),
				new LineTo((XY)line.EndPoint),
			};

			this._log.Add(line.Handle, line.SubclassMarker, RenderStatus.Rendered, "Rendered as Path.");
			return new PathNode(line.Handle, segs, stroke, fill: null);
		}

		private PathNode buildPolyline(IPolyline polyline, double styleScaleToPaper, InsertRenderContext? containingInsert)
		{
			Entity polyEntity = (Entity)polyline;
			var pts = polyline.GetPoints<XYZ>(this._configuration.ArcPrecision).ToArray();
			if (pts.Length < 2)
			{
				this._log.Add(polyEntity.Handle, polyEntity.SubclassMarker, RenderStatus.Skipped, "Degenerate polyline.");
				return null;
			}

			var stroke = resolveStroke(polyEntity, styleScaleToPaper, containingInsert);
			var segs = new List<PathSegment>(pts.Length + 2);
			segs.Add(new MoveTo((XY)pts[0]));
			for (int i = 1; i < pts.Length; i++)
			{
				segs.Add(new LineTo((XY)pts[i]));
			}

			if (polyline.IsClosed)
			{
				segs.Add(new ClosePath());
			}

			this._log.Add(polyEntity.Handle, polyEntity.SubclassMarker, RenderStatus.Rendered, "Rendered as Path.");
			return new PathNode(polyEntity.Handle, segs, stroke, fill: null);
		}

		private PathNode buildArc(Arc arc, double styleScaleToPaper, InsertRenderContext? containingInsert)
		{
			var pts = arc.PolygonalVertexes(this._configuration.ArcPrecision).ToArray();
			if (pts.Length < 2)
			{
				this._log.Add(arc.Handle, arc.SubclassMarker, RenderStatus.Skipped, "Degenerate arc.");
				return null;
			}

			var stroke = resolveStroke(arc, styleScaleToPaper, containingInsert);
			var segs = new List<PathSegment>(pts.Length);
			segs.Add(new MoveTo((XY)pts[0]));
			for (int i = 1; i < pts.Length; i++)
			{
				segs.Add(new LineTo((XY)pts[i]));
			}

			this._log.Add(arc.Handle, arc.SubclassMarker, RenderStatus.Rendered, "Rendered as polygonal Path (Stage 00).");
			return new PathNode(arc.Handle, segs, stroke, fill: null);
		}

		private PathNode buildEllipse(Ellipse ellipse, double styleScaleToPaper, InsertRenderContext? containingInsert)
		{
			var pts = ellipse.PolygonalVertexes(this._configuration.ArcPrecision).ToArray();
			if (pts.Length < 2)
			{
				this._log.Add(ellipse.Handle, ellipse.SubclassMarker, RenderStatus.Skipped, "Degenerate ellipse.");
				return null;
			}

			var stroke = resolveStroke(ellipse, styleScaleToPaper, containingInsert);
			var segs = new List<PathSegment>(pts.Length);
			segs.Add(new MoveTo((XY)pts[0]));
			for (int i = 1; i < pts.Length; i++)
			{
				segs.Add(new LineTo((XY)pts[i]));
			}

			this._log.Add(ellipse.Handle, ellipse.SubclassMarker, RenderStatus.Rendered, "Rendered as polygonal Path (Stage 00).");
			return new PathNode(ellipse.Handle, segs, stroke, fill: null);
		}

		private PathNode buildCircle(Circle circle, double styleScaleToPaper, InsertRenderContext? containingInsert)
		{
			var pts = circle.PolygonalVertexes(this._configuration.ArcPrecision).ToArray();
			if (pts.Length < 2)
			{
				this._log.Add(circle.Handle, circle.SubclassMarker, RenderStatus.Skipped, "Degenerate circle.");
				return null;
			}

			var stroke = resolveStroke(circle, styleScaleToPaper, containingInsert);
			var segs = new List<PathSegment>(pts.Length + 1);
			segs.Add(new MoveTo((XY)pts[0]));
			for (int i = 1; i < pts.Length; i++)
			{
				segs.Add(new LineTo((XY)pts[i]));
			}
			segs.Add(new ClosePath());

			this._log.Add(circle.Handle, circle.SubclassMarker, RenderStatus.Rendered, "Rendered as polygonal Path (Stage 00).");
			return new PathNode(circle.Handle, segs, stroke, fill: null);
		}

			private PathNode buildPoint(Point point, double pointScaleToPaper, InsertRenderContext? containingInsert)
			{
				double sizePaper = this._configuration.DotSize;
				double sizeLocal = pointScaleToPaper <= 0 ? sizePaper : sizePaper / pointScaleToPaper;
				double diff = sizeLocal / 2;

				XY p = (XY)point.Location;
				XY min = new XY(p.X - diff, p.Y - diff);
				XY max = new XY(p.X + diff, p.Y + diff);

				var fillColor = resolveStroke(point, pointScaleToPaper, containingInsert).Color;
				var fill = new FillStyle(fillColor);

			var rect = rectanglePath(point.Handle, min, max);
			this._log.Add(point.Handle, point.SubclassMarker, RenderStatus.Rendered, "Rendered as filled rectangle (dot).");
			return new PathNode(point.Handle, rect.Segments, stroke: null, fill: fill);
		}

		private TextRunNode buildText(TextEntity text, double textScaleToPaper, InsertRenderContext? containingInsert)
		{
			ACadSharp.Color color = resolveStroke(text, textScaleToPaper, containingInsert).Color;
			TextRunNode node = this._textLayout.LayoutText(text, textScaleToPaper, color);
			if (node == null)
			{
				return null;
			}

			this._log.Add(text.Handle, text.SubclassMarker, RenderStatus.Rendered, "Rendered as TextRun.");
			return node;
		}

		private RenderNode buildMText(MText mtext, double textScaleToPaper, InsertRenderContext? containingInsert)
		{
			ACadSharp.Color color = resolveStroke(mtext, textScaleToPaper, containingInsert).Color;
			RenderNode node = this._textLayout.LayoutMText(mtext, textScaleToPaper, color);
			if (node == null)
			{
				return null;
			}

			this._log.Add(mtext.Handle, mtext.SubclassMarker, RenderStatus.Rendered, "Rendered as MTEXT runs.");
			return node;
		}

		private RenderNode buildDimension(
			Dimension dimension,
			Viewport viewport,
			double styleScaleToPaper,
			double textScaleToPaper,
			InsertRenderContext? containingInsert,
			Matrix4 parentTransform,
			int depth,
			HashSet<string> activeBlocks)
		{
			if (dimension == null)
			{
				return null;
			}

			if (tryBuildDimensionFromAnonymousBlock(dimension, viewport, styleScaleToPaper, textScaleToPaper, containingInsert, parentTransform, depth, activeBlocks, out RenderNode blockNode))
			{
				this._log.Add(dimension.Handle, dimension.SubclassMarker, RenderStatus.Rendered, "Rendered via anonymous dimension block.");
				return blockNode;
			}

			DimensionStyle style = dimension.GetActiveDimensionStyle() ?? dimension.Style ?? DimensionStyle.Default;
			var nodes = new List<RenderNode>();

			switch (dimension)
			{
				case DimensionLinear linear:
					buildLinearOrAlignedDimension(linear, isLinear: true, style, nodes, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);
					break;
				case DimensionAligned aligned:
					buildLinearOrAlignedDimension(aligned, isLinear: false, style, nodes, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);
					break;
				case DimensionAngular3Pt angular3Pt:
					buildAngular3PointDimension(angular3Pt, style, nodes, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);
					break;
				case DimensionAngular2Line angular2Line:
					buildAngular2LineDimension(angular2Line, style, nodes, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);
					break;
				case DimensionRadius radius:
					buildRadiusDimension(radius, style, nodes, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);
					break;
				case DimensionDiameter diameter:
					buildDiameterDimension(diameter, style, nodes, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);
					break;
				case DimensionOrdinate ordinate:
					buildOrdinateDimension(ordinate, style, nodes, styleScaleToPaper, textScaleToPaper, containingInsert);
					break;
				default:
					this._log.Add(dimension.Handle, dimension.SubclassMarker, RenderStatus.NotImplemented, "DIMENSION subtype not supported in Stage 03.");
					this._configuration.Notify($"[{dimension.SubclassMarker}] Dimension subtype not implemented (scene graph pipeline).", NotificationType.NotImplemented);
					return null;
			}

			if (nodes.Count == 0)
			{
				this._log.Add(dimension.Handle, dimension.SubclassMarker, RenderStatus.Skipped, "Dimension produced no visible primitives.");
				return null;
			}

			this._log.Add(dimension.Handle, dimension.SubclassMarker, RenderStatus.Rendered, "Rendered as computed dimension geometry.");
			return new GroupNode(dimension.Handle, Matrix4.Identity, nodes);
		}

		private bool tryBuildDimensionFromAnonymousBlock(
			Dimension dimension,
			Viewport viewport,
			double styleScaleToPaper,
			double textScaleToPaper,
			InsertRenderContext? containingInsert,
			Matrix4 parentTransform,
			int depth,
			HashSet<string> activeBlocks,
			out RenderNode node)
		{
			node = null;

			BlockRecord block = dimension.Block;
			if (block == null || block.Entities == null || block.Entities.Count == 0)
			{
				return false;
			}

			string blockKey = getBlockKey(block);
			if (activeBlocks.Contains(blockKey))
			{
				this._log.Add(dimension.Handle, dimension.SubclassMarker, RenderStatus.Error, $"Circular DIMENSION block detected for '{block.Name}'.");
				return false;
			}

			activeBlocks.Add(blockKey);
			try
			{
				var children = new List<RenderNode>();
				foreach (Entity blockEntity in block.Entities)
				{
					if (blockEntity == null || blockEntity is AttributeDefinition)
					{
						continue;
					}

					RenderNode child = buildEntityNode(
						blockEntity,
						viewport,
						styleScaleToPaper,
						textScaleToPaper,
						containingInsert,
						parentTransform,
						depth + 1,
						activeBlocks);

					if (child != null)
					{
						children.Add(child);
					}
				}

				if (children.Count == 0)
				{
					return false;
				}

				node = new GroupNode(dimension.Handle, Matrix4.Identity, children);
				return true;
			}
			finally
			{
				activeBlocks.Remove(blockKey);
			}
		}

		private void buildLinearOrAlignedDimension(
			DimensionAligned dimension,
			bool isLinear,
			DimensionStyle style,
			List<RenderNode> nodes,
			double styleScaleToPaper,
			double textScaleToPaper,
			InsertRenderContext? containingInsert,
			Viewport viewport,
			int depth,
			HashSet<string> activeBlocks)
		{
			XY p1 = (XY)dimension.FirstPoint;
			XY p2 = (XY)dimension.SecondPoint;
			XY dimLinePoint = (XY)dimension.DefinitionPoint;

			XY dimDirection;
			if (isLinear)
			{
				// Rotation is defined in the dimension's OCS; map it into WCS.
				double angle = ((DimensionLinear)dimension).Rotation;
				XYZ ocs = new XYZ(Math.Cos(angle), Math.Sin(angle), 0.0);
				XYZ wcs = TransformHelper.OcsToWcs(dimension.Normal) * ocs;
				dimDirection = new XY(wcs.X, wcs.Y);
			}
			else
			{
				dimDirection = p2 - p1;
			}

			if (!tryNormalize(dimDirection, out dimDirection))
			{
				dimDirection = XY.AxisX;
			}

			XY perp = perpendicularLeft(dimDirection);
			double projection1 = dot(dimLinePoint - p1, perp);
			double projection2 = dot(dimLinePoint - p2, perp);
			XY d1 = p1 + perp * projection1;
			XY d2 = p2 + perp * projection2;

			double scale = dimensionScale(style);
			double extOffset = style.ExtensionLineOffset * scale;
			double extExtension = style.ExtensionLineExtension * scale;
			double gapRaw = style.DimensionLineGap * scale;
			double gap = Math.Abs(gapRaw);
			bool boxText = gapRaw < 0.0;
			double arrowSize = Math.Max(0.0, style.ArrowSize * scale);

			StrokeStyle extStroke1 = createDimensionStroke(dimension, style.ExtensionLineColor, style.ExtensionLineWeight, style.LineTypeExt1, styleScaleToPaper, containingInsert);
			StrokeStyle extStroke2 = createDimensionStroke(dimension, style.ExtensionLineColor, style.ExtensionLineWeight, style.LineTypeExt2, styleScaleToPaper, containingInsert);
			StrokeStyle dimStroke = createDimensionStroke(dimension, style.DimensionLineColor, style.DimensionLineWeight, style.LineType, styleScaleToPaper, containingInsert);

			double side1 = Math.Abs(projection1) < 1e-9 ? 1.0 : Math.Sign(projection1);
			double side2 = Math.Abs(projection2) < 1e-9 ? 1.0 : Math.Sign(projection2);

			XY extDir = perp;
			if (Math.Abs(dimension.ExtLineRotation) > 1e-12)
			{
				extDir = rotate(perp, dimension.ExtLineRotation);
				if (!tryNormalize(extDir, out extDir))
				{
					extDir = perp;
				}
			}

			if (!style.SuppressFirstExtensionLine)
			{
				XY start = p1 + extDir * (side1 * extOffset);
				XY end = d1 + extDir * (side1 * extExtension);
				PathNode path = createLinePath(dimension.Handle, start, end, extStroke1);
				if (path != null)
				{
					nodes.Add(path);
				}
			}

			if (!style.SuppressSecondExtensionLine)
			{
				XY start = p2 + extDir * (side2 * extOffset);
				XY end = d2 + extDir * (side2 * extExtension);
				PathNode path = createLinePath(dimension.Handle, start, end, extStroke2);
				if (path != null)
				{
					nodes.Add(path);
				}
			}

			if (!tryNormalize(d2 - d1, out XY axis))
			{
				axis = dimDirection;
			}

			XY mid = new XY((d1.X + d2.X) * 0.5, (d1.Y + d2.Y) * 0.5);
			string text = getDimensionText(dimension, style);

			double span = distance(d1, d2);
			double textHeight = Math.Max(0.0, style.TextHeight * scale);
			double textWidth = estimateTextWidth(text, textHeight);
			bool canTextFitInside = text != null && (textWidth + 2.0 * gap) <= span;
			bool canArrowsFitInside = (2.0 * arrowSize + 2.0 * gap) <= span;

			bool textOutside = false;
			bool arrowsOutside = !canArrowsFitInside;
			if (text != null)
			{
				bool forceInside = style.TextInsideExtensions;
				bool textInside = forceInside || canTextFitInside;
				if (textInside && canArrowsFitInside)
				{
					textOutside = false;
					arrowsOutside = false;
				}
				else
				{
					switch (style.DimensionTextArrowFit)
					{
						case TextArrowFitType.Both:
							textOutside = true;
							arrowsOutside = true;
							break;
						case TextArrowFitType.ArrowsFirst:
							arrowsOutside = !canArrowsFitInside;
							textOutside = !textInside;
							break;
						case TextArrowFitType.TextFirst:
							textOutside = !textInside;
							arrowsOutside = !canArrowsFitInside;
							break;
						case TextArrowFitType.BestFit:
						default:
							if (canArrowsFitInside && !textInside)
							{
								textOutside = true;
								arrowsOutside = false;
							}
							else if (!canArrowsFitInside && textInside)
							{
								textOutside = false;
								arrowsOutside = true;
							}
							else
							{
								textOutside = true;
								arrowsOutside = true;
							}
							break;
					}
				}
			}

			double dimLineExt = 0.0;
			if (style.TickSize * scale > 1e-9 && style.DimensionLineExtension > 1e-12)
			{
				dimLineExt = style.DimensionLineExtension * scale;
			}

			XY dimStart = d1 - axis * dimLineExt;
			XY dimEnd = d2 + axis * dimLineExt;

			bool drawDimLineInside = !(textOutside && !style.TextOutsideExtensions);
			if (drawDimLineInside && (!style.SuppressFirstDimensionLine || !style.SuppressSecondDimensionLine))
			{
				bool breakForText = !dimension.IsTextUserDefinedLocation
					&& !textOutside
					&& text != null
					&& style.TextVerticalAlignment == DimensionTextVerticalAlignment.Centered
					&& textWidth > 1e-9;

				if (breakForText)
				{
					double breakHalf = (textWidth * 0.5) + gap;
					XY breakA = mid - axis * breakHalf;
					XY breakB = mid + axis * breakHalf;

					if (!style.SuppressFirstDimensionLine)
					{
						PathNode left = createLinePath(dimension.Handle, dimStart, breakA, dimStroke);
						if (left != null)
						{
							nodes.Add(left);
						}
					}

					if (!style.SuppressSecondDimensionLine)
					{
						PathNode right = createLinePath(dimension.Handle, breakB, dimEnd, dimStroke);
						if (right != null)
						{
							nodes.Add(right);
						}
					}
				}
				else
				{
					if (!style.SuppressFirstDimensionLine && !style.SuppressSecondDimensionLine)
					{
						PathNode full = createLinePath(dimension.Handle, dimStart, dimEnd, dimStroke);
						if (full != null)
						{
							nodes.Add(full);
						}
					}
					else if (style.SuppressFirstDimensionLine && !style.SuppressSecondDimensionLine)
					{
						PathNode half = createLinePath(dimension.Handle, mid, dimEnd, dimStroke);
						if (half != null)
						{
							nodes.Add(half);
						}
					}
					else if (!style.SuppressFirstDimensionLine && style.SuppressSecondDimensionLine)
					{
						PathNode half = createLinePath(dimension.Handle, dimStart, mid, dimStroke);
						if (half != null)
						{
							nodes.Add(half);
						}
					}
				}
			}

			BlockRecord arrow1Block = style.SeparateArrowBlocks ? style.DimArrow1 : style.ArrowBlock;
			BlockRecord arrow2Block = style.SeparateArrowBlocks ? style.DimArrow2 : style.ArrowBlock;

			if (!style.SuppressFirstDimensionLine)
			{
				XY arrow1Dir = arrowsOutside ? -axis : axis;
				if (dimension.FlipArrow1)
				{
					arrow1Dir = -arrow1Dir;
				}
				addDimensionArrow(nodes, dimension, style, arrow1Block, d1, arrow1Dir, arrowSize, dimStroke, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);
			}

			if (!style.SuppressSecondDimensionLine)
			{
				XY arrow2Dir = arrowsOutside ? axis : -axis;
				if (dimension.FlipArrow2)
				{
					arrow2Dir = -arrow2Dir;
				}
				addDimensionArrow(nodes, dimension, style, arrow2Block, d2, arrow2Dir, arrowSize, dimStroke, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);
			}

			if (text == null)
			{
				return;
			}

			XY textPos;
			bool userTextLocation = dimension.IsTextUserDefinedLocation;
			if (userTextLocation)
			{
				textPos = toOcsWorldPoint(dimension.TextMiddlePoint, dimension.Normal);
			}
			else
			{
				textPos = mid;
				double textOffset = gap + (textHeight * 0.5);
				double dimSide = Math.Sign(dot(dimLinePoint - mid, perp));
				if (Math.Abs(dimSide) < 1e-9)
				{
					dimSide = 1.0;
				}

					switch (style.TextVerticalAlignment)
					{
						case DimensionTextVerticalAlignment.Below:
							textPos += perp * (-dimSide * textOffset);
						break;
					case DimensionTextVerticalAlignment.Centered:
						break;
					default:
							textPos += perp * (dimSide * textOffset);
							break;
					}

					// Preserve vertical placement when shifting the text along the dimension axis.
					XY verticalOffset = textPos - mid;

					if (textOutside)
					{
						switch (style.TextHorizontalAlignment)
						{
							case DimensionTextHorizontalAlignment.Left:
							case DimensionTextHorizontalAlignment.OverFirstExtLine:
								textPos = (d1 - axis * (arrowSize + gap + (textWidth * 0.5))) + verticalOffset;
								break;
							case DimensionTextHorizontalAlignment.Right:
							case DimensionTextHorizontalAlignment.OverSecondExtLine:
							default:
								textPos = (d2 + axis * (arrowSize + gap + (textWidth * 0.5))) + verticalOffset;
								break;
						}
					}
					else
					{
						switch (style.TextHorizontalAlignment)
						{
							case DimensionTextHorizontalAlignment.Left:
							case DimensionTextHorizontalAlignment.OverFirstExtLine:
								textPos = (d1 + axis * (gap + arrowSize + (textWidth * 0.5))) + verticalOffset;
								break;
							case DimensionTextHorizontalAlignment.Right:
							case DimensionTextHorizontalAlignment.OverSecondExtLine:
								textPos = (d2 - axis * (gap + arrowSize + (textWidth * 0.5))) + verticalOffset;
								break;
						}
					}

				if (textOutside && style.TextMovement == TextMovement.AddLeaderWhenTextMoved)
				{
					PathNode leader = createLinePath(dimension.Handle, mid, textPos, dimStroke);
					if (leader != null)
					{
						nodes.Add(leader);
					}
				}
			}

			double textRotation = Math.Atan2(axis.Y, axis.X);
			if ((!textOutside && style.TextInsideHorizontal) || (textOutside && style.TextOutsideHorizontal))
			{
				textRotation = 0.0;
			}

			addDimensionTextNode(nodes, dimension, style, text, textPos, textRotation, textScaleToPaper, containingInsert);

			if (boxText && !userTextLocation)
			{
				addDimensionTextBox(nodes, dimension, textPos, textRotation, textWidth, textHeight, gap, dimStroke);
			}
		}

		private void buildAngular3PointDimension(
			DimensionAngular3Pt dimension,
			DimensionStyle style,
			List<RenderNode> nodes,
			double styleScaleToPaper,
			double textScaleToPaper,
			InsertRenderContext? containingInsert,
			Viewport viewport,
			int depth,
			HashSet<string> activeBlocks)
		{
			XY center = (XY)dimension.AngleVertex;
			XY p1 = (XY)dimension.FirstPoint;
			XY p2 = (XY)dimension.SecondPoint;
			XY selector = (XY)dimension.DefinitionPoint;

			double radius = distance(center, selector);
			if (radius < 1e-9)
			{
				radius = Math.Max(distance(center, p1), distance(center, p2));
			}

			if (radius < 1e-9)
			{
				return;
			}

			double a1 = Math.Atan2(p1.Y - center.Y, p1.X - center.X);
			double a2 = Math.Atan2(p2.Y - center.Y, p2.X - center.X);
			double selectorAngle = Math.Atan2(selector.Y - center.Y, selector.X - center.X);
			selectArcThroughAngle(a1, a2, selectorAngle, out double start, out double end, out double sweep);

				StrokeStyle dimStroke = createDimensionStroke(dimension, style.DimensionLineColor, style.DimensionLineWeight, style.LineType, styleScaleToPaper, containingInsert);
				StrokeStyle extStroke = createDimensionStroke(dimension, style.ExtensionLineColor, style.ExtensionLineWeight, style.LineTypeExt1, styleScaleToPaper, containingInsert);

			List<XY> arcPoints = buildArcPoints(center, radius, start, end);
			PathNode arcPath = createPolylinePath(dimension.Handle, arcPoints, dimStroke, closed: false);
			if (arcPath != null)
			{
				nodes.Add(arcPath);
			}

			XY arcStart = polarPoint(center, radius, start);
			XY arcEnd = polarPoint(center, radius, end);
			PathNode ext1 = createLinePath(dimension.Handle, center, arcStart, extStroke);
			PathNode ext2 = createLinePath(dimension.Handle, center, arcEnd, extStroke);
			if (ext1 != null)
			{
				nodes.Add(ext1);
			}
			if (ext2 != null)
			{
				nodes.Add(ext2);
			}

			double scale = dimensionScale(style);
			double arrowSize = Math.Max(0.0, style.ArrowSize * scale);
			XY tangentStart = new XY(-Math.Sin(start), Math.Cos(start));
			XY tangentEnd = new XY(-Math.Sin(end), Math.Cos(end));
			BlockRecord arrow1Block = style.SeparateArrowBlocks ? style.DimArrow1 : style.ArrowBlock;
			BlockRecord arrow2Block = style.SeparateArrowBlocks ? style.DimArrow2 : style.ArrowBlock;

			addDimensionArrow(nodes, dimension, style, arrow1Block, arcStart, tangentStart, arrowSize, dimStroke, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);
			addDimensionArrow(nodes, dimension, style, arrow2Block, arcEnd, -tangentEnd, arrowSize, dimStroke, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);

			string text = getDimensionText(dimension, style);
			if (text == null)
			{
				return;
			}

			XY textPos;
			if (dimension.IsTextUserDefinedLocation)
			{
				textPos = toOcsWorldPoint(dimension.TextMiddlePoint, dimension.Normal);
			}
			else
			{
				double textAngle = start + sweep * 0.5;
				double textRadius = radius + Math.Abs(style.DimensionLineGap * scale) + style.TextHeight * scale * 0.5;
				textPos = polarPoint(center, textRadius, textAngle);
			}

				double rotation = style.TextOutsideHorizontal ? 0.0 : (start + sweep * 0.5 + Math.PI * 0.5);
				addDimensionTextNode(nodes, dimension, style, text, textPos, rotation, textScaleToPaper, containingInsert);
			}

		private void buildAngular2LineDimension(
			DimensionAngular2Line dimension,
			DimensionStyle style,
			List<RenderNode> nodes,
			double styleScaleToPaper,
			double textScaleToPaper,
			InsertRenderContext? containingInsert,
			Viewport viewport,
			int depth,
			HashSet<string> activeBlocks)
		{
			XY a1 = (XY)dimension.FirstPoint;
			XY a2 = (XY)dimension.SecondPoint;
			XY b1 = (XY)dimension.AngleVertex;
			XY b2 = (XY)dimension.DefinitionPoint;

			if (!tryIntersectInfiniteLines(a1, a2, b1, b2, out XY center))
			{
				center = b1;
			}

			XY selectorPoint = (XY)dimension.DimensionArc;
			double radius = distance(center, selectorPoint);
			if (radius < 1e-9)
			{
				radius = Math.Max(distance(center, a1), distance(center, b2));
			}

			if (radius < 1e-9)
			{
				return;
			}

			double angle1 = Math.Atan2(a2.Y - a1.Y, a2.X - a1.X);
			double angle2 = Math.Atan2(b2.Y - b1.Y, b2.X - b1.X);
			double selectorAngle = Math.Atan2(selectorPoint.Y - center.Y, selectorPoint.X - center.X);
			selectArcThroughAngle(angle1, angle2, selectorAngle, out double start, out double end, out double sweep);

				StrokeStyle dimStroke = createDimensionStroke(dimension, style.DimensionLineColor, style.DimensionLineWeight, style.LineType, styleScaleToPaper, containingInsert);
				StrokeStyle extStroke = createDimensionStroke(dimension, style.ExtensionLineColor, style.ExtensionLineWeight, style.LineTypeExt1, styleScaleToPaper, containingInsert);

			List<XY> arcPoints = buildArcPoints(center, radius, start, end);
			PathNode arcPath = createPolylinePath(dimension.Handle, arcPoints, dimStroke, closed: false);
			if (arcPath != null)
			{
				nodes.Add(arcPath);
			}

			XY arcStart = polarPoint(center, radius, start);
			XY arcEnd = polarPoint(center, radius, end);
			PathNode ext1 = createLinePath(dimension.Handle, center, arcStart, extStroke);
			PathNode ext2 = createLinePath(dimension.Handle, center, arcEnd, extStroke);
			if (ext1 != null)
			{
				nodes.Add(ext1);
			}
			if (ext2 != null)
			{
				nodes.Add(ext2);
			}

			double scale = dimensionScale(style);
			double arrowSize = Math.Max(0.0, style.ArrowSize * scale);
			XY tangentStart = new XY(-Math.Sin(start), Math.Cos(start));
			XY tangentEnd = new XY(-Math.Sin(end), Math.Cos(end));
			BlockRecord arrow1Block = style.SeparateArrowBlocks ? style.DimArrow1 : style.ArrowBlock;
			BlockRecord arrow2Block = style.SeparateArrowBlocks ? style.DimArrow2 : style.ArrowBlock;

			addDimensionArrow(nodes, dimension, style, arrow1Block, arcStart, tangentStart, arrowSize, dimStroke, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);
			addDimensionArrow(nodes, dimension, style, arrow2Block, arcEnd, -tangentEnd, arrowSize, dimStroke, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);

			string text = getDimensionText(dimension, style);
			if (text == null)
			{
				return;
			}

			XY textPos;
			if (dimension.IsTextUserDefinedLocation)
			{
				textPos = toOcsWorldPoint(dimension.TextMiddlePoint, dimension.Normal);
			}
			else
			{
				double textAngle = start + sweep * 0.5;
				double textRadius = radius + Math.Abs(style.DimensionLineGap * scale) + style.TextHeight * scale * 0.5;
				textPos = polarPoint(center, textRadius, textAngle);
			}

			double rotation = style.TextOutsideHorizontal ? 0.0 : (start + sweep * 0.5 + Math.PI * 0.5);
			addDimensionTextNode(nodes, dimension, style, text, textPos, rotation, textScaleToPaper, containingInsert);
		}

		private void buildRadiusDimension(
			DimensionRadius dimension,
			DimensionStyle style,
			List<RenderNode> nodes,
			double styleScaleToPaper,
			double textScaleToPaper,
			InsertRenderContext? containingInsert,
			Viewport viewport,
			int depth,
			HashSet<string> activeBlocks)
		{
			XY center = (XY)dimension.DefinitionPoint;
			XY edge = (XY)dimension.AngleVertex;
			if (!tryNormalize(edge - center, out XY dir))
			{
				return;
			}

				StrokeStyle dimStroke = createDimensionStroke(dimension, style.DimensionLineColor, style.DimensionLineWeight, style.LineType, styleScaleToPaper, containingInsert);
			PathNode radial = createLinePath(dimension.Handle, center, edge, dimStroke);
			if (radial != null)
			{
				nodes.Add(radial);
			}

			double scale = dimensionScale(style);
			double arrowSize = Math.Max(0.0, style.ArrowSize * scale);
			BlockRecord arrowBlock = style.SeparateArrowBlocks ? style.DimArrow1 : style.ArrowBlock;
			addDimensionArrow(nodes, dimension, style, arrowBlock, edge, -dir, arrowSize, dimStroke, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);

				addCenterMark(nodes, dimension, style, center, styleScaleToPaper, containingInsert);

			string text = getDimensionText(dimension, style);
			if (text == null)
			{
				return;
			}

			XY textPos;
			if (dimension.IsTextUserDefinedLocation)
			{
				textPos = toOcsWorldPoint(dimension.TextMiddlePoint, dimension.Normal);
			}
			else
			{
					double radius = distance(center, edge);
					double gap = Math.Abs(style.DimensionLineGap * scale);
					double outsideDistance = radius + Math.Max(style.TextHeight * scale, arrowSize);

					// DIMTIX behaves differently for radial/diameter dimensions: when set, it forces text outside.
					double textDistance = style.TextInsideExtensions ? outsideDistance : radius * 0.5;
					textPos = center + dir * (textDistance + gap);
				}

				bool isOutside = distance(center, textPos) > distance(center, edge) + 1e-9;
				bool forceHorizontal = isOutside ? style.TextOutsideHorizontal : style.TextInsideHorizontal;
				double rotation = forceHorizontal ? 0.0 : Math.Atan2(dir.Y, dir.X);
				addDimensionTextNode(nodes, dimension, style, text, textPos, rotation, textScaleToPaper, containingInsert);
			}

		private void buildDiameterDimension(
			DimensionDiameter dimension,
			DimensionStyle style,
			List<RenderNode> nodes,
			double styleScaleToPaper,
			double textScaleToPaper,
			InsertRenderContext? containingInsert,
			Viewport viewport,
			int depth,
			HashSet<string> activeBlocks)
		{
			XY first = (XY)dimension.AngleVertex;
			XY second = (XY)dimension.DefinitionPoint;
			if (!tryNormalize(second - first, out XY dir))
			{
				return;
			}

				StrokeStyle dimStroke = createDimensionStroke(dimension, style.DimensionLineColor, style.DimensionLineWeight, style.LineType, styleScaleToPaper, containingInsert);
			PathNode diameter = createLinePath(dimension.Handle, first, second, dimStroke);
			if (diameter != null)
			{
				nodes.Add(diameter);
			}

			double scale = dimensionScale(style);
			double arrowSize = Math.Max(0.0, style.ArrowSize * scale);
			BlockRecord arrow1Block = style.SeparateArrowBlocks ? style.DimArrow1 : style.ArrowBlock;
			BlockRecord arrow2Block = style.SeparateArrowBlocks ? style.DimArrow2 : style.ArrowBlock;
			addDimensionArrow(nodes, dimension, style, arrow1Block, first, dir, arrowSize, dimStroke, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);
			addDimensionArrow(nodes, dimension, style, arrow2Block, second, -dir, arrowSize, dimStroke, styleScaleToPaper, textScaleToPaper, containingInsert, viewport, depth, activeBlocks);

				addCenterMark(nodes, dimension, style, new XY((first.X + second.X) * 0.5, (first.Y + second.Y) * 0.5), styleScaleToPaper, containingInsert);

			string text = getDimensionText(dimension, style);
			if (text == null)
			{
				return;
			}

			XY textPos;
			if (dimension.IsTextUserDefinedLocation)
			{
				textPos = toOcsWorldPoint(dimension.TextMiddlePoint, dimension.Normal);
			}
			else
			{
				double gap = Math.Abs(style.DimensionLineGap * scale) + style.TextHeight * scale * 0.5;
				XY center = new XY((first.X + second.X) * 0.5, (first.Y + second.Y) * 0.5);
				textPos = center + perpendicularLeft(dir) * gap;
			}

				double rotation = style.TextOutsideHorizontal ? 0.0 : Math.Atan2(dir.Y, dir.X);
				addDimensionTextNode(nodes, dimension, style, text, textPos, rotation, textScaleToPaper, containingInsert);
			}

		private void buildOrdinateDimension(
			DimensionOrdinate dimension,
			DimensionStyle style,
			List<RenderNode> nodes,
			double styleScaleToPaper,
			double textScaleToPaper,
			InsertRenderContext? containingInsert)
		{
			double scale = dimensionScale(style);
			double minOffset = 2.0 * style.ArrowSize * scale;
			XY ref1 = (XY)dimension.FeatureLocation;
			XY ref2 = (XY)dimension.LeaderEndpoint;
			XY refDim = ref2 - ref1;
			double rotation = dimension.HorizontalDirection;
			int side = 1;

			if (dimension.IsOrdinateTypeX)
			{
				rotation += Math.PI * 0.5;
			}

			XY ocsDimRef = rotate(refDim, -rotation);
			XY p1;
			XY p2;

			if (ocsDimRef.X >= 0.0)
			{
				if (ocsDimRef.X >= 2.0 * minOffset)
				{
					p1 = new XY(ocsDimRef.X - minOffset, 0.0);
					p2 = new XY(ocsDimRef.X - minOffset, ocsDimRef.Y);
				}
				else
				{
					p1 = new XY(minOffset, 0.0);
					p2 = new XY(ocsDimRef.X - minOffset, ocsDimRef.Y);
				}
			}
			else
			{
				if (ocsDimRef.X <= -2.0 * minOffset)
				{
					p1 = new XY(ocsDimRef.X + minOffset, 0.0);
					p2 = new XY(ocsDimRef.X + minOffset, ocsDimRef.Y);
				}
				else
				{
					p1 = new XY(-minOffset, 0.0);
					p2 = new XY(ocsDimRef.X + minOffset, ocsDimRef.Y);
				}
				side = -1;
			}

			p1 = ref1 + rotate(p1, rotation);
			p2 = ref1 + rotate(p2, rotation);
			XY start = polarPoint(ref1, style.ExtensionLineOffset * scale, rotation);

				StrokeStyle stroke = createDimensionStroke(dimension, style.DimensionLineColor, style.DimensionLineWeight, style.LineType, styleScaleToPaper, containingInsert);
			PathNode segment1 = createLinePath(dimension.Handle, start, p1, stroke);
			PathNode segment2 = createLinePath(dimension.Handle, p1, p2, stroke);
			PathNode segment3 = createLinePath(dimension.Handle, p2, ref2, stroke);

			if (segment1 != null)
			{
				nodes.Add(segment1);
			}
			if (segment2 != null)
			{
				nodes.Add(segment2);
			}
			if (segment3 != null)
			{
				nodes.Add(segment3);
			}

			string text = getDimensionText(dimension, style);
			if (text == null)
			{
				return;
			}

			XY textPos = dimension.IsTextUserDefinedLocation
				? toOcsWorldPoint(dimension.TextMiddlePoint, dimension.Normal)
				: polarPoint(ref2, side * Math.Abs(style.DimensionLineGap * scale), rotation);

			double textRotation = style.TextInsideHorizontal || style.TextOutsideHorizontal ? 0.0 : rotation;
			addDimensionTextNode(nodes, dimension, style, text, textPos, textRotation, textScaleToPaper, containingInsert);
		}

			private void addCenterMark(List<RenderNode> nodes, Dimension dimension, DimensionStyle style, XY center, double styleScaleToPaper, InsertRenderContext? containingInsert)
			{
				double scale = dimensionScale(style);
				double size = Math.Abs(style.CenterMarkSize * scale);
				if (size < 1e-9)
				{
					return;
				}

				StrokeStyle stroke = createDimensionStroke(dimension, style.ExtensionLineColor, style.ExtensionLineWeight, style.LineTypeExt1, styleScaleToPaper, containingInsert);
				PathNode xAxis = createLinePath(dimension.Handle, new XY(center.X - size, center.Y), new XY(center.X + size, center.Y), stroke);
				PathNode yAxis = createLinePath(dimension.Handle, new XY(center.X, center.Y - size), new XY(center.X, center.Y + size), stroke);
				if (xAxis != null)
				{
					nodes.Add(xAxis);
			}
			if (yAxis != null)
			{
				nodes.Add(yAxis);
			}
		}

		private void addDimensionArrow(
			List<RenderNode> nodes,
			Dimension dimension,
			DimensionStyle style,
			BlockRecord arrowBlock,
			XY tip,
			XY direction,
			double arrowSize,
			StrokeStyle dimStroke,
			double styleScaleToPaper,
			double textScaleToPaper,
			InsertRenderContext? containingInsert,
			Viewport viewport,
			int depth,
			HashSet<string> activeBlocks)
		{
			if (arrowSize <= 1e-9 || !tryNormalize(direction, out XY dir))
			{
				return;
			}

			double tickSize = style.TickSize * dimensionScale(style);
			if (tickSize > 1e-9)
			{
				XY perp = perpendicularLeft(dir);
				XY diag = dir + perp;
				if (!tryNormalize(diag, out diag))
				{
					diag = perp;
				}

				XY a = tip - diag * (0.5 * tickSize);
				XY b = tip + diag * (0.5 * tickSize);
				PathNode tick = createLinePath(dimension.Handle, a, b, dimStroke);
				if (tick != null)
				{
					nodes.Add(tick);
				}
				return;
			}

				if (arrowBlock != null)
				{
					var insert = new Insert(arrowBlock)
					{
						InsertPoint = new XYZ(tip.X, tip.Y, 0.0),
						XScale = arrowSize,
						YScale = arrowSize,
						ZScale = 1.0,
						Rotation = Math.Atan2(dir.Y, dir.X),
						Layer = dimension.Layer,
						// Force explicit, already-resolved styling so arrow blocks don't accidentally inherit ByBlock from the
						// surrounding INSERT context (the arrow insert is an implementation detail, not a nested block).
						Color = dimStroke.Color,
						LineWeight = LineWeightType.ByLayer,
						LineType = LineType.ByLayer,
						Normal = dimension.Normal,
					};

					RenderNode arrowNode = buildEntityNode(
						insert,
						viewport,
						styleScaleToPaper,
						textScaleToPaper,
						containingInsert: null,
						parentTransform: Matrix4.Identity,
						depth: depth + 1,
						activeBlocks: activeBlocks);

				if (arrowNode != null)
				{
					nodes.Add(arrowNode);
					return;
				}
			}

			XY perpDir = perpendicularLeft(dir);
			XY back = tip - dir * arrowSize;
			XY p1 = back + perpDir * (arrowSize * 0.3);
			XY p2 = back - perpDir * (arrowSize * 0.3);
			var segs = new PathSegment[]
			{
				new MoveTo(tip),
				new LineTo(p1),
				new LineTo(p2),
				new ClosePath(),
			};

			nodes.Add(new PathNode(
				dimension.Handle,
				segs,
				stroke: null,
				fill: new FillStyle(dimStroke.Color)));
		}

			private void addDimensionTextNode(
				List<RenderNode> nodes,
				Dimension dimension,
				DimensionStyle style,
				string text,
			XY position,
			double rotation,
			double textScaleToPaper,
			InsertRenderContext? containingInsert)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			double scale = dimensionScale(style);
			double height = Math.Max(1e-6, style.TextHeight * scale);
			double finalRotation = normalizeAngleSigned(rotation + dimension.TextRotation);
			var textEntity = new TextEntity
			{
				Value = text,
				Height = height,
				InsertPoint = new XYZ(position.X, position.Y, 0.0),
				AlignmentPoint = new XYZ(position.X, position.Y, 0.0),
				HorizontalAlignment = TextHorizontalAlignment.Center,
				VerticalAlignment = TextVerticalAlignmentType.Middle,
				Rotation = finalRotation,
				Style = style.Style ?? TextStyle.Default,
				Color = style.TextColor,
				Layer = dimension.Layer,
				Normal = dimension.Normal,
			};

			TextRunNode node = buildText(textEntity, textScaleToPaper, containingInsert);
			if (node != null)
			{
				nodes.Add(node);
				}
			}

			private static double estimateTextWidth(string text, double height)
			{
				if (string.IsNullOrEmpty(text) || height <= 1e-12)
				{
					return 0.0;
				}

				// Keep this consistent with TextLayoutEngine's internal ApproximateTextMetrics.
				const double averageGlyphWidth = 0.55;
				return text.Length * averageGlyphWidth * height;
			}

			private static void addDimensionTextBox(
				List<RenderNode> nodes,
				Dimension dimension,
				XY center,
				double rotation,
				double textWidth,
				double textHeight,
				double gap,
				StrokeStyle stroke)
			{
				if (nodes == null || dimension == null || stroke == null)
				{
					return;
				}

				double halfW = (textWidth * 0.5) + gap;
				double halfH = (textHeight * 0.5) + gap;
				if (halfW <= 1e-9 || halfH <= 1e-9)
				{
					return;
				}

				XY xAxis = new XY(Math.Cos(rotation), Math.Sin(rotation));
				XY yAxis = perpendicularLeft(xAxis);

				XY p1 = center - xAxis * halfW - yAxis * halfH;
				XY p2 = center + xAxis * halfW - yAxis * halfH;
				XY p3 = center + xAxis * halfW + yAxis * halfH;
				XY p4 = center - xAxis * halfW + yAxis * halfH;

				var segs = new PathSegment[]
				{
					new MoveTo(p1),
					new LineTo(p2),
					new LineTo(p3),
					new LineTo(p4),
					new ClosePath(),
				};

				nodes.Add(new PathNode(dimension.Handle, segs, stroke, fill: null));
			}

			private static string getDimensionText(Dimension dimension, DimensionStyle style)
			{
				if (dimension == null)
				{
				return null;
			}

			if (string.Equals(dimension.Text, " ", StringComparison.Ordinal))
			{
				return null;
			}

			string value = dimension.GetMeasurementText(style);
			if (string.IsNullOrWhiteSpace(value))
			{
				return null;
			}

			return value;
		}

		private StrokeStyle createDimensionStroke(
			Dimension dimension,
			ACadSharp.Color color,
			LineWeightType lineWeight,
			LineType lineType,
			double styleScaleToPaper,
			InsertRenderContext? containingInsert)
		{
			// Use the same style resolver as normal entities so ByLayer/ByBlock and dash patterns behave consistently.
			// The synthetic entity isn't part of the document graph, so bake $LTSCALE into LineTypeScale.
			double globalLtScale = dimension?.Document?.Header?.LineTypeScale ?? 1.0;
			var proxy = new Line
			{
				StartPoint = XYZ.Zero,
				EndPoint = XY.AxisX.Convert<XYZ>(),
				Layer = dimension?.Layer,
				Color = color,
				LineWeight = lineWeight,
				LineType = lineType ?? LineType.ByLayer,
				LineTypeScale = globalLtScale,
			};

			return resolveStroke(proxy, styleScaleToPaper, containingInsert);
		}

		private static PathNode createLinePath(ulong handle, XY start, XY end, StrokeStyle stroke)
		{
			if (distance(start, end) < 1e-9 || stroke == null)
			{
				return null;
			}

			var segs = new PathSegment[]
			{
				new MoveTo(start),
				new LineTo(end),
			};

			return new PathNode(handle, segs, stroke, fill: null);
		}

		private static PathNode createPolylinePath(ulong handle, IReadOnlyList<XY> points, StrokeStyle stroke, bool closed)
		{
			if (points == null || points.Count < 2 || stroke == null)
			{
				return null;
			}

			var segs = new List<PathSegment>(points.Count + 1)
			{
				new MoveTo(points[0])
			};

			for (int i = 1; i < points.Count; i++)
			{
				segs.Add(new LineTo(points[i]));
			}

			if (closed)
			{
				segs.Add(new ClosePath());
			}

			return new PathNode(handle, segs, stroke, fill: null);
		}

		private List<XY> buildArcPoints(XY center, double radius, double startAngle, double endAngle)
		{
			if (radius < 1e-9)
			{
				return new List<XY>();
			}

			double sweep = endAngle - startAngle;
			if (sweep <= 0.0)
			{
				sweep += Math.PI * 2.0;
			}

			int segments = Math.Max(8, (int)Math.Ceiling(this._configuration.ArcPrecision * (sweep / (Math.PI * 2.0))));
			segments = Math.Min(2048, segments);

			var points = new List<XY>(segments + 1);
			for (int i = 0; i <= segments; i++)
			{
				double t = (double)i / segments;
				double angle = startAngle + sweep * t;
				points.Add(polarPoint(center, radius, angle));
			}

			return points;
		}

		private static void selectArcThroughAngle(double angleA, double angleB, double selector, out double start, out double end, out double sweep)
		{
			double a = normalizeAnglePositive(angleA);
			double b = normalizeAnglePositive(angleB);
			double s = normalizeAnglePositive(selector);

			if (isAngleOnArcCounterClockwise(a, b, s))
			{
				start = a;
				end = a + deltaCounterClockwise(a, b);
			}
			else
			{
				start = b;
				end = b + deltaCounterClockwise(b, a);
			}

			sweep = end - start;
		}

		private static bool isAngleOnArcCounterClockwise(double start, double end, double angle)
		{
			double total = deltaCounterClockwise(start, end);
			double part = deltaCounterClockwise(start, angle);
			return part <= total + 1e-9;
		}

		private static double deltaCounterClockwise(double start, double end)
		{
			double delta = normalizeAnglePositive(end) - normalizeAnglePositive(start);
			if (delta < 0.0)
			{
				delta += Math.PI * 2.0;
			}

			return delta;
		}

		private static bool tryIntersectInfiniteLines(XY a1, XY a2, XY b1, XY b2, out XY intersection)
		{
			double x1 = a1.X;
			double y1 = a1.Y;
			double x2 = a2.X;
			double y2 = a2.Y;
			double x3 = b1.X;
			double y3 = b1.Y;
			double x4 = b2.X;
			double y4 = b2.Y;

			double denominator = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
			if (Math.Abs(denominator) < 1e-12)
			{
				intersection = XY.Zero;
				return false;
			}

			double det1 = x1 * y2 - y1 * x2;
			double det2 = x3 * y4 - y3 * x4;
			double px = (det1 * (x3 - x4) - (x1 - x2) * det2) / denominator;
			double py = (det1 * (y3 - y4) - (y1 - y2) * det2) / denominator;
			intersection = new XY(px, py);
			return true;
		}

		private static XY toOcsWorldPoint(XYZ point, XYZ normal)
		{
			XYZ world = TransformHelper.OcsToWcs(normal) * point;
			return new XY(world.X, world.Y);
		}

		private static double dimensionScale(DimensionStyle style)
		{
			if (style == null || style.ScaleFactor <= 1e-9)
			{
				return 1.0;
			}

			return style.ScaleFactor;
		}

		private static XY polarPoint(XY origin, double radius, double angle)
		{
			return new XY(
				origin.X + radius * Math.Cos(angle),
				origin.Y + radius * Math.Sin(angle));
		}

		private static XY rotate(XY value, double angle)
		{
			double c = Math.Cos(angle);
			double s = Math.Sin(angle);
			return new XY(
				value.X * c - value.Y * s,
				value.X * s + value.Y * c);
		}

		private static XY perpendicularLeft(XY value)
		{
			return new XY(-value.Y, value.X);
		}

		private static bool tryNormalize(XY value, out XY normalized)
		{
			double len = Math.Sqrt(value.X * value.X + value.Y * value.Y);
			if (len < 1e-12)
			{
				normalized = XY.Zero;
				return false;
			}

			normalized = new XY(value.X / len, value.Y / len);
			return true;
		}

		private static double dot(XY a, XY b)
		{
			return a.X * b.X + a.Y * b.Y;
		}

		private static double distance(XY a, XY b)
		{
			double dx = a.X - b.X;
			double dy = a.Y - b.Y;
			return Math.Sqrt(dx * dx + dy * dy);
		}

		private static string getBlockKey(BlockRecord block)
		{
			if (block == null)
			{
				return string.Empty;
			}

			if (!string.IsNullOrWhiteSpace(block.Name))
			{
				return block.Name;
			}

			return "#" + block.Handle.ToString();
		}

		private static double normalizeAnglePositive(double angle)
		{
			double twoPi = Math.PI * 2.0;
			double result = angle % twoPi;
			if (result < 0.0)
			{
				result += twoPi;
			}

			return result;
		}

		private static double normalizeAngleSigned(double angle)
		{
			double value = normalizeAnglePositive(angle);
			if (value > Math.PI)
			{
				value -= Math.PI * 2.0;
			}

			return value;
		}

		private static PathNode rectanglePath(ulong handle, XY min, XY max)
		{
			var segs = new PathSegment[]
			{
				new MoveTo(new XY(min.X, min.Y)),
				new LineTo(new XY(max.X, min.Y)),
				new LineTo(new XY(max.X, max.Y)),
				new LineTo(new XY(min.X, max.Y)),
				new ClosePath(),
			};
			return new PathNode(handle, segs, stroke: null, fill: null);
		}
	}
}
