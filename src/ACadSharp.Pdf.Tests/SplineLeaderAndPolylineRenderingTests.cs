using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Pdf;
using ACadSharp.Pdf.Core;
using ACadSharp.Tables;
using CSMath;
using System.Linq;
using Xunit;

namespace ACadSharp.Pdf.Tests
{
	public class SplineLeaderAndPolylineRenderingTests
	{
		[Fact]
		public void Spline_RendersAsPolygonalPath()
		{
			var spline = new Spline
			{
				Layer = new Layer("0") { Color = Color.Red },
				Degree = 3,
			};
			spline.ControlPoints.Add(new XYZ(0, 0, 0));
			spline.ControlPoints.Add(new XYZ(10, 20, 0));
			spline.ControlPoints.Add(new XYZ(20, 20, 0));
			spline.ControlPoints.Add(new XYZ(30, 0, 0));

			string content = renderEntity(spline, out var log);

			Assert.Contains(log.Entries, e => e.Handle == spline.Handle && e.Status == Core.Render.RenderStatus.Rendered);
			Assert.Contains(" m", content);
			Assert.Contains(" l", content);
		}

		[Fact]
		public void Leader_RendersPathAndHookline()
		{
			var leader = new Leader
			{
				Layer = new Layer("0") { Color = Color.Blue },
				CreationType = LeaderCreationType.CreatedWithTextAnnotation,
				AnnotationOffset = new XYZ(5, 0, 0),
			};
			leader.Vertices.Add(new XYZ(0, 0, 0));
			leader.Vertices.Add(new XYZ(20, 0, 0));

			string content = renderEntity(leader, out var log);

			Assert.Contains(log.Entries, e => e.Handle == leader.Handle && e.Status == Core.Render.RenderStatus.Rendered);
			Assert.Contains("0 0 m", content);
			Assert.Contains("20 0 l", content);
			Assert.Contains("25 0 l", content);
		}

		[Fact]
		public void DegenerateBulgePolyline_DoesNotFailRender()
		{
			var polyline = new LwPolyline();
			polyline.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)) { Bulge = 1.0 });
			polyline.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)));

			string content = renderEntity(polyline, out var log);

			Assert.DoesNotContain(log.Entries, e => e.Status == Core.Render.RenderStatus.Error);
			Assert.Contains(log.Entries, e => e.Handle == polyline.Handle && e.Status == Core.Render.RenderStatus.Rendered);
			Assert.Contains("0 0 m", content);
		}

		[Fact]
		public void Wipeout_RendersWhiteMask()
		{
			var wipeout = new Wipeout
			{
				Layer = new Layer("0") { Color = Color.Green },
				InsertPoint = new XYZ(10, 20, 0),
				UVector = new XYZ(40, 0, 0),
				VVector = new XYZ(0, 20, 0),
				Size = new XY(1, 1),
				ClippingState = true,
				ClipType = ClipType.Polygonal,
			};
			wipeout.ClipBoundaryVertices.Add(new XY(-0.5, -0.5));
			wipeout.ClipBoundaryVertices.Add(new XY(0.5, -0.5));
			wipeout.ClipBoundaryVertices.Add(new XY(0.5, 0.5));
			wipeout.ClipBoundaryVertices.Add(new XY(-0.5, 0.5));
			wipeout.ClipBoundaryVertices.Add(new XY(-0.5, -0.5));

			string content = renderEntity(wipeout, out var log);

			Assert.Contains(log.Entries, e => e.Handle == wipeout.Handle && e.Status == Core.Render.RenderStatus.Rendered);
			Assert.Contains("1 1 1 rg", content);
			Assert.Contains("f", content);
		}

		private static string renderEntity(Entity entity, out Core.Render.RenderLog log)
		{
			var pdf = new PdfDocument();
			var page = pdf.Pages.AddPage();
			page.Layout = new Layout("L")
			{
				PaperUnits = PlotPaperUnits.Pixels,
				DenominatorScale = 1.0,
				PaperWidth = 500,
				PaperHeight = 500,
			};
			page.Entities.Add(entity);

			var cfg = new PdfConfiguration
			{
				UseSceneGraph = true,
				DecimalFormat = "0.####",
			};

			string content = page.Contents.GetPdfForm(cfg);
			log = cfg.LastRenderLog;
			return content;
		}
	}
}
