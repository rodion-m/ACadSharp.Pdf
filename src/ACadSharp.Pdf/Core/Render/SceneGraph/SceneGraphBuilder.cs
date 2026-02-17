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
