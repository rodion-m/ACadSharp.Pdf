using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Pdf.Core.Render.Style;
using ACadSharp.Pdf.Core.Render.Transforms;
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

		public SceneGraphBuilder(Layout layout, PdfConfiguration configuration, PropertyResolver resolver, RenderLog log)
		{
			this._layout = layout ?? throw new ArgumentNullException(nameof(layout));
			this._configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
			this._resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
			this._log = log ?? throw new ArgumentNullException(nameof(log));
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
					var en = buildEntityNode(e, viewport: null, geometricScaleToPaper: 1.0);
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
				var child = buildEntityNode(e, viewport, viewport.ScaleFactor);
				if (child != null)
				{
					children.Add(child);
				}
			}

			var group = new GroupNode(viewport.Handle, modelToPaper, children);
			return new ClipNode(viewport.Handle, clipPath, new[] { group });
		}

		private RenderNode buildEntityNode(Entity entity, Viewport viewport, double geometricScaleToPaper)
		{
			if (entity == null)
			{
				return null;
			}

			var vis = this._resolver.GetVisibility(entity, viewport);
			if (vis != VisibilityDecision.Visible)
			{
				this._log.Add(entity.Handle, entity.SubclassMarker, RenderStatus.Skipped, $"Visibility gate: {vis}");
				return null;
			}

			try
			{
				switch (entity)
				{
					case Line line:
						return buildLine(line, geometricScaleToPaper);
					case Arc arc:
						return buildArc(arc, geometricScaleToPaper);
					case Circle circle:
						return buildCircle(circle, geometricScaleToPaper);
					case Ellipse ellipse:
						return buildEllipse(ellipse, geometricScaleToPaper);
					case Point point:
						return buildPoint(point, geometricScaleToPaper);
					case IPolyline polyline:
						return buildPolyline(polyline, geometricScaleToPaper);
					case TextEntity text:
						return buildText(text, geometricScaleToPaper);
					default:
						this._log.Add(entity.Handle, entity.SubclassMarker, RenderStatus.NotImplemented, "Entity not supported in Stage 00 frontend.");
						this._configuration.Notify($"[{entity.SubclassMarker}] Drawing not implemented (scene graph pipeline).", NotificationType.NotImplemented);
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

		private PathNode buildLine(Line line, double geometricScaleToPaper)
		{
			var stroke = this._resolver.ResolveStroke(line, this._layout, geometricScaleToPaper);
			var segs = new PathSegment[]
			{
				new MoveTo((XY)line.StartPoint),
				new LineTo((XY)line.EndPoint),
			};

			this._log.Add(line.Handle, line.SubclassMarker, RenderStatus.Rendered, "Rendered as Path.");
			return new PathNode(line.Handle, segs, stroke, fill: null);
		}

		private PathNode buildPolyline(IPolyline polyline, double geometricScaleToPaper)
		{
			Entity polyEntity = (Entity)polyline;
			var pts = polyline.GetPoints<XYZ>(this._configuration.ArcPrecision).ToArray();
			if (pts.Length < 2)
			{
				this._log.Add(polyEntity.Handle, polyEntity.SubclassMarker, RenderStatus.Skipped, "Degenerate polyline.");
				return null;
			}

			var stroke = this._resolver.ResolveStroke(polyEntity, this._layout, geometricScaleToPaper);
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

		private PathNode buildArc(Arc arc, double geometricScaleToPaper)
		{
			var pts = arc.PolygonalVertexes(this._configuration.ArcPrecision).ToArray();
			if (pts.Length < 2)
			{
				this._log.Add(arc.Handle, arc.SubclassMarker, RenderStatus.Skipped, "Degenerate arc.");
				return null;
			}

			var stroke = this._resolver.ResolveStroke(arc, this._layout, geometricScaleToPaper);
			var segs = new List<PathSegment>(pts.Length);
			segs.Add(new MoveTo((XY)pts[0]));
			for (int i = 1; i < pts.Length; i++)
			{
				segs.Add(new LineTo((XY)pts[i]));
			}

			this._log.Add(arc.Handle, arc.SubclassMarker, RenderStatus.Rendered, "Rendered as polygonal Path (Stage 00).");
			return new PathNode(arc.Handle, segs, stroke, fill: null);
		}

		private PathNode buildEllipse(Ellipse ellipse, double geometricScaleToPaper)
		{
			var pts = ellipse.PolygonalVertexes(this._configuration.ArcPrecision).ToArray();
			if (pts.Length < 2)
			{
				this._log.Add(ellipse.Handle, ellipse.SubclassMarker, RenderStatus.Skipped, "Degenerate ellipse.");
				return null;
			}

			var stroke = this._resolver.ResolveStroke(ellipse, this._layout, geometricScaleToPaper);
			var segs = new List<PathSegment>(pts.Length);
			segs.Add(new MoveTo((XY)pts[0]));
			for (int i = 1; i < pts.Length; i++)
			{
				segs.Add(new LineTo((XY)pts[i]));
			}

			this._log.Add(ellipse.Handle, ellipse.SubclassMarker, RenderStatus.Rendered, "Rendered as polygonal Path (Stage 00).");
			return new PathNode(ellipse.Handle, segs, stroke, fill: null);
		}

		private PathNode buildCircle(Circle circle, double geometricScaleToPaper)
		{
			var pts = circle.PolygonalVertexes(this._configuration.ArcPrecision).ToArray();
			if (pts.Length < 2)
			{
				this._log.Add(circle.Handle, circle.SubclassMarker, RenderStatus.Skipped, "Degenerate circle.");
				return null;
			}

			var stroke = this._resolver.ResolveStroke(circle, this._layout, geometricScaleToPaper);
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

		private PathNode buildPoint(Point point, double geometricScaleToPaper)
		{
			double sizePaper = this._configuration.DotSize;
			double sizeLocal = geometricScaleToPaper <= 0 ? sizePaper : sizePaper / geometricScaleToPaper;
			double diff = sizeLocal / 2;

			XY p = (XY)point.Location;
			XY min = new XY(p.X - diff, p.Y - diff);
			XY max = new XY(p.X + diff, p.Y + diff);

			var fillColor = this._resolver.ResolveStroke(point, this._layout, geometricScaleToPaper).Color;
			var fill = new FillStyle(fillColor);

			var rect = rectanglePath(point.Handle, min, max);
			this._log.Add(point.Handle, point.SubclassMarker, RenderStatus.Rendered, "Rendered as filled rectangle (dot).");
			return new PathNode(point.Handle, rect.Segments, stroke: null, fill: fill);
		}

		private TextRunNode buildText(TextEntity text, double geometricScaleToPaper)
		{
			// Resolve final font size in PDF points. TEXT height is in current space units.
			double heightPaperUnits = text.Height * geometricScaleToPaper;
			double fontSizePt = TransformHelper.PaperToPdfPoints(heightPaperUnits, this._layout);

			ACadSharp.Color color = this._resolver.ResolveStroke(text, this._layout, geometricScaleToPaper).Color;
			// Keep anchor in local space; transforms (viewport/blocks) are applied by the flattener.
			XY anchorLocal = (XY)text.InsertPoint;

			TextAlignment h = TextAlignment.Left;
			switch (text.HorizontalAlignment)
			{
				case TextHorizontalAlignment.Center:
					h = TextAlignment.Center;
					break;
				case TextHorizontalAlignment.Right:
					h = TextAlignment.Right;
					break;
			}

			TextVAlignment v = TextVAlignment.Baseline;
			switch (text.VerticalAlignment)
			{
				case TextVerticalAlignmentType.Bottom:
					v = TextVAlignment.Bottom;
					break;
				case TextVerticalAlignmentType.Middle:
					v = TextVAlignment.Middle;
					break;
				case TextVerticalAlignmentType.Top:
					v = TextVAlignment.Top;
					break;
			}

			this._log.Add(text.Handle, text.SubclassMarker, RenderStatus.Rendered, "Rendered as TextRun (simple).");
			return new TextRunNode(
				text.Handle,
				text.Value ?? string.Empty,
				fontName: text.Style?.Name ?? "F1",
				fontSizePt: fontSizePt,
				anchorPt: anchorLocal,
				rotationRad: text.Rotation,
				obliqueRad: text.ObliqueAngle,
				widthFactor: text.WidthFactor <= 0 ? 1.0 : text.WidthFactor,
				color: color,
				hAlign: h,
				vAlign: v);
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
