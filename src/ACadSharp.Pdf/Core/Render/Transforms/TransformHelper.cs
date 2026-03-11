using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Pdf.Extensions;
using CSMath;
using System;
using System.Globalization;

namespace ACadSharp.Pdf.Core.Render.Transforms
{
	public static class TransformHelper
	{
		// DXF Arbitrary Axis Algorithm lives in CSMath.Matrix4.GetArbitraryAxis; keep a thin wrapper to make usage explicit.
		public static Matrix4 OcsToWcs(XYZ normal)
		{
			return Matrix4.GetArbitraryAxis(normal);
		}

		public static Matrix4 ViewportModelToPaper(Viewport viewport)
		{
			if (viewport == null) throw new ArgumentNullException(nameof(viewport));

			var boxPaper = viewport.GetBoundingBox();
			var boxModel = GetViewportModelBoundingBox(viewport);

			double s = viewport.ScaleFactor;
			var df = boxModel.Min * s;

			XYZ translation = boxPaper.Min - df;
			Matrix4 t = Matrix4.CreateTranslation(translation);
			Matrix4 scale = Matrix4.CreateScale(new XYZ(s, s, 1));

			// Apply scale first, then translate: (T * S) * p
			return t * scale;
		}

		public static BoundingBox GetViewportModelBoundingBox(Viewport viewport)
		{
			if (viewport == null) throw new ArgumentNullException(nameof(viewport));

			// For paper-space viewports, ACadSharp's ViewCenter is stored in view coordinates relative
			// to the viewport target, not as an absolute model-space center. Using it directly drops
			// the actual publication view on real DWGs and selects only a small legend cluster.
			double centerX = viewport.ViewCenter.X + viewport.ViewTarget.X;
			double centerY = viewport.ViewCenter.Y + viewport.ViewTarget.Y;

			XYZ min = new XYZ(centerX - viewport.ViewWidth / 2.0, centerY - viewport.ViewHeight / 2.0, 0.0);
			XYZ max = new XYZ(centerX + viewport.ViewWidth / 2.0, centerY + viewport.ViewHeight / 2.0, 0.0);
			return new BoundingBox(min, max);
		}

		public static double PaperToPdfPoints(double valuePaperUnits, Layout layout)
		{
			if (layout == null) throw new ArgumentNullException(nameof(layout));
			return (valuePaperUnits / layout.DenominatorScale).ToPdfUnit(layout.PaperUnits);
		}

		public static XY PaperToPdfPoints(XY pointPaperUnits, Layout layout)
		{
			return new XY(
				PaperToPdfPoints(pointPaperUnits.X, layout),
				PaperToPdfPoints(pointPaperUnits.Y, layout));
		}

		public static Matrix4 CreateShearXByY(double shear)
		{
			// x' = x + shear*y
			Matrix4 m = Matrix4.Identity;
			m.M10 = shear;
			return m;
		}

		public static Matrix4 ImagePixelToWcs(XYZ insertPoint, XYZ uVector, XYZ vVector)
		{
			// Maps pixel-space (x,y,0,1) to WCS: p = insert + x*u + y*v
			Matrix4 m = Matrix4.Identity;

			m.M00 = uVector.X;
			m.M01 = uVector.Y;
			m.M02 = uVector.Z;

			m.M10 = vVector.X;
			m.M11 = vVector.Y;
			m.M12 = vVector.Z;

			m.M30 = insertPoint.X;
			m.M31 = insertPoint.Y;
			m.M32 = insertPoint.Z;

			return m;
		}

		public static string ToPdfDouble(double value, PdfConfiguration configuration)
		{
			if (configuration == null) throw new ArgumentNullException(nameof(configuration));
			return value.ToString(configuration.DecimalFormat, CultureInfo.InvariantCulture);
		}
	}
}
