using ACadSharp.Objects;
using ACadSharp.Pdf.Core.Render.Transforms;
using CSMath;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ACadSharp.Pdf.Core.Render.Flattening
{
	internal sealed class SceneFlattener
	{
		private readonly Layout _layout;

		public SceneFlattener(Layout layout)
		{
			this._layout = layout ?? throw new ArgumentNullException(nameof(layout));
		}

		public IReadOnlyList<FlatDrawCommand> Flatten(IReadOnlyList<RenderNode> nodes)
		{
			var commands = new List<FlatDrawCommand>();
			if (nodes == null || nodes.Count == 0)
			{
				return commands;
			}

			Matrix4 identity = Matrix4.Identity;
			foreach (var node in nodes)
			{
				flattenNode(node, identity, commands);
			}

			return commands;
		}

		private void flattenNode(RenderNode node, Matrix4 current, List<FlatDrawCommand> commands)
		{
			switch (node)
			{
				case GroupNode group:
					{
						Matrix4 next = current * group.Transform;
						foreach (var child in group.Children)
						{
							flattenNode(child, next, commands);
						}
						break;
					}
				case ClipNode clip:
					{
						var clipSegs = transformAndConvertSegments(clip.ClipPath.Segments, current);
						commands.Add(new FlatBeginClipCommand(clipSegs));
						foreach (var child in clip.Children)
						{
							flattenNode(child, current, commands);
						}
						commands.Add(new FlatEndClipCommand());
						break;
					}
				case PathNode path:
					{
						var segs = transformAndConvertSegments(path.Segments, current);
						commands.Add(new FlatPathCommand(segs, path.Stroke, path.Fill));
						break;
					}
				case TextRunNode text:
					{
						XY anchorPaper = transformPoint(text.AnchorPt, current);
						XY anchorPdf = TransformHelper.PaperToPdfPoints(anchorPaper, this._layout);
						if (requiresOutlineFallback(text.Text) && tryAddOutlinedText(text, anchorPdf, commands))
						{
							break;
						}

						commands.Add(new FlatTextCommand(
							text: text.Text,
							fontSizePt: text.FontSizePt,
							anchorPdfPt: anchorPdf,
							a: text.A,
							b: text.B,
							c: text.C,
							d: text.D,
							color: text.Color));
						break;
					}
				case ImageNode image:
					{
						FlatImageCommand cmd = createFlatImageCommand(image, current);
						if (cmd != null)
						{
							commands.Add(cmd);
						}
						break;
					}
				default:
					break;
			}
		}

		private FlatImageCommand createFlatImageCommand(ImageNode image, Matrix4 current)
		{
			if (image == null || image.Rgb24Data == null || image.Rgb24Data.Length == 0)
			{
				return null;
			}

			XY originPaper = transformPoint(XY.Zero, current);
			XY axisXPaper = transformPoint(new XY(1.0, 0.0), current);
			XY axisYPaper = transformPoint(new XY(0.0, 1.0), current);

			XY originPdf = TransformHelper.PaperToPdfPoints(originPaper, this._layout);
			XY axisXPdf = TransformHelper.PaperToPdfPoints(axisXPaper, this._layout);
			XY axisYPdf = TransformHelper.PaperToPdfPoints(axisYPaper, this._layout);

			double a = axisXPdf.X - originPdf.X;
			double b = axisXPdf.Y - originPdf.Y;
			double c = axisYPdf.X - originPdf.X;
			double d = axisYPdf.Y - originPdf.Y;

			return new FlatImageCommand(
				rgb24Data: image.Rgb24Data,
				sourceWidthPixels: image.SourceWidthPixels,
				sourceHeightPixels: image.SourceHeightPixels,
				displayWidth: image.DisplayWidth,
				displayHeight: image.DisplayHeight,
				a: a,
				b: b,
				c: c,
				d: d,
				e: originPdf.X,
				f: originPdf.Y);
		}

		private IReadOnlyList<PathSegment> transformAndConvertSegments(IReadOnlyList<PathSegment> segments, Matrix4 matrix)
		{
			if (segments == null || segments.Count == 0)
			{
				return Array.Empty<PathSegment>();
			}

			var result = new List<PathSegment>(segments.Count);
			foreach (var seg in segments)
			{
				switch (seg)
				{
					case MoveTo m:
						result.Add(new MoveTo(TransformHelper.PaperToPdfPoints(transformPoint(m.Point, matrix), this._layout)));
						break;
					case LineTo l:
						result.Add(new LineTo(TransformHelper.PaperToPdfPoints(transformPoint(l.Point, matrix), this._layout)));
						break;
					case CubicTo c:
						result.Add(new CubicTo(
							TransformHelper.PaperToPdfPoints(transformPoint(c.C1, matrix), this._layout),
							TransformHelper.PaperToPdfPoints(transformPoint(c.C2, matrix), this._layout),
							TransformHelper.PaperToPdfPoints(transformPoint(c.End, matrix), this._layout)));
						break;
					case ClosePath:
						result.Add(new ClosePath());
						break;
				}
			}

			return result;
		}

		private static XY transformPoint(XY p, Matrix4 m)
		{
			XYZ v = m * new XYZ(p.X, p.Y, 0.0);
			return new XY(v.X, v.Y);
		}

		private static bool requiresOutlineFallback(string text)
		{
			return !string.IsNullOrEmpty(text) && text.Any(c => c > 0x00FF);
		}

		private static bool tryAddOutlinedText(TextRunNode text, XY anchorPdf, List<FlatDrawCommand> commands)
		{
			if (string.IsNullOrEmpty(text.Text))
			{
				return false;
			}

			using SKTypeface typeface = SKTypeface.FromFamilyName(text.FontName) ?? SKTypeface.Default;
			using SKFont font = new SKFont(typeface, (float)text.FontSizePt);
			using SKPath path = font.GetTextPath(text.Text, new SKPoint(0, 0));
			if (path == null || path.IsEmpty)
			{
				return false;
			}

			IReadOnlyList<PathSegment> segments = flattenSkiaPath(path, text, anchorPdf);
			if (segments == null || segments.Count == 0)
			{
				return false;
			}

			commands.Add(new FlatPathCommand(segments, stroke: null, fill: new FillStyle(text.Color)));
			return true;
		}

		private static IReadOnlyList<PathSegment> flattenSkiaPath(SKPath path, TextRunNode text, XY anchorPdf)
		{
			List<PathSegment> segments = new List<PathSegment>();
			using SKPath.RawIterator iterator = path.CreateRawIterator();
			SKPoint[] points = new SKPoint[4];
			SKPoint contourStart = default;
			SKPoint current = default;
			bool hasCurrent = false;

			while (true)
			{
				SKPathVerb verb = iterator.Next(points);
				if (verb == SKPathVerb.Done)
				{
					break;
				}

				switch (verb)
				{
					case SKPathVerb.Move:
						contourStart = points[0];
						current = points[0];
						hasCurrent = true;
						segments.Add(new MoveTo(transformGlyphPoint(points[0], text, anchorPdf)));
						break;
					case SKPathVerb.Line:
						if (!hasCurrent)
						{
							contourStart = points[0];
							current = points[0];
							hasCurrent = true;
							segments.Add(new MoveTo(transformGlyphPoint(points[0], text, anchorPdf)));
						}
						current = points[1];
						segments.Add(new LineTo(transformGlyphPoint(points[1], text, anchorPdf)));
						break;
					case SKPathVerb.Quad:
						appendQuadraticAsLines(segments, current, points[1], points[2], text, anchorPdf);
						current = points[2];
						break;
					case SKPathVerb.Conic:
						appendConicAsLines(segments, iterator.ConicWeight(), current, points[1], points[2], text, anchorPdf);
						current = points[2];
						break;
					case SKPathVerb.Cubic:
						appendCubicAsLines(segments, current, points[1], points[2], points[3], text, anchorPdf);
						current = points[3];
						break;
					case SKPathVerb.Close:
						if (hasCurrent && (Math.Abs(current.X - contourStart.X) > 1e-6 || Math.Abs(current.Y - contourStart.Y) > 1e-6))
						{
							segments.Add(new LineTo(transformGlyphPoint(contourStart, text, anchorPdf)));
						}
						segments.Add(new ClosePath());
						current = contourStart;
						break;
				}
			}

			return segments;
		}

		private static void appendQuadraticAsLines(List<PathSegment> segments, SKPoint p0, SKPoint p1, SKPoint p2, TextRunNode text, XY anchorPdf)
		{
			const int steps = 12;
			for (int i = 1; i <= steps; i++)
			{
				double t = (double)i / steps;
				double mt = 1.0 - t;
				SKPoint pt = new SKPoint(
					(float)((mt * mt * p0.X) + (2.0 * mt * t * p1.X) + (t * t * p2.X)),
					(float)((mt * mt * p0.Y) + (2.0 * mt * t * p1.Y) + (t * t * p2.Y)));
				segments.Add(new LineTo(transformGlyphPoint(pt, text, anchorPdf)));
			}
		}

		private static void appendConicAsLines(List<PathSegment> segments, float weight, SKPoint p0, SKPoint p1, SKPoint p2, TextRunNode text, XY anchorPdf)
		{
			const int steps = 16;
			for (int i = 1; i <= steps; i++)
			{
				double t = (double)i / steps;
				double mt = 1.0 - t;
				double denom = (mt * mt) + (2.0 * weight * mt * t) + (t * t);
				double x = ((mt * mt * p0.X) + (2.0 * weight * mt * t * p1.X) + (t * t * p2.X)) / denom;
				double y = ((mt * mt * p0.Y) + (2.0 * weight * mt * t * p1.Y) + (t * t * p2.Y)) / denom;
				segments.Add(new LineTo(transformGlyphPoint(new SKPoint((float)x, (float)y), text, anchorPdf)));
			}
		}

		private static void appendCubicAsLines(List<PathSegment> segments, SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, TextRunNode text, XY anchorPdf)
		{
			const int steps = 16;
			for (int i = 1; i <= steps; i++)
			{
				double t = (double)i / steps;
				double mt = 1.0 - t;
				double x =
					(mt * mt * mt * p0.X) +
					(3.0 * mt * mt * t * p1.X) +
					(3.0 * mt * t * t * p2.X) +
					(t * t * t * p3.X);
				double y =
					(mt * mt * mt * p0.Y) +
					(3.0 * mt * mt * t * p1.Y) +
					(3.0 * mt * t * t * p2.Y) +
					(t * t * t * p3.Y);
				segments.Add(new LineTo(transformGlyphPoint(new SKPoint((float)x, (float)y), text, anchorPdf)));
			}
		}

		private static XY transformGlyphPoint(SKPoint point, TextRunNode text, XY anchorPdf)
		{
			double x = anchorPdf.X + (text.A * point.X) - (text.C * point.Y);
			double y = anchorPdf.Y + (text.B * point.X) - (text.D * point.Y);
			return new XY(x, y);
		}
	}
}
