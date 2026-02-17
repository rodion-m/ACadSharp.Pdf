using ACadSharp.Objects;
using ACadSharp.Pdf.Core.Render.Transforms;
using CSMath;
using System;
using System.Collections.Generic;

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
						commands.Add(new FlatTextCommand(
							text: text.Text,
							fontSizePt: text.FontSizePt,
							anchorPdfPt: anchorPdf,
							rotationRad: text.RotationRad,
							obliqueRad: text.ObliqueRad,
							widthFactor: text.WidthFactor,
							color: text.Color));
						break;
					}
				default:
					break;
			}
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
	}
}
