using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Pdf;
using ACadSharp.Pdf.Core;
using ACadSharp.Tables;
using CSMath;
using System;
using System.IO.Compression;
using System.Text;
using System.IO;
using System.Linq;
using Xunit;

namespace ACadSharp.Pdf.Tests
{
	public class FocusedModelWindowTests
	{
		[Fact]
		public void AddModelWindow_RendersOnlyFocusedWindow()
		{
			var doc = new CadDocument();
			var inside = new Line
			{
				StartPoint = new XYZ(10, 10, 0),
				EndPoint = new XYZ(20, 10, 0),
				Layer = new Layer("0") { Color = Color.Red },
			};
			var outside = new Line
			{
				StartPoint = new XYZ(500, 500, 0),
				EndPoint = new XYZ(600, 500, 0),
				Layer = new Layer("0") { Color = Color.Blue },
			};

			doc.Entities.Add(inside);
			doc.Entities.Add(outside);

			using var stream = new MemoryStream();
			var exporter = new PdfExporter(stream);
			exporter.Configuration.UseSceneGraph = true;

			var layout = new Layout("Focused")
			{
				PaperUnits = PlotPaperUnits.Millimeters,
				PaperWidth = 200,
				PaperHeight = 100,
				DenominatorScale = 1.0,
			};

			PdfPage page = exporter.AddModelWindow(
				doc,
				layout,
				new BoundingBox(new XYZ(0, 0, 0), new XYZ(100, 100, 0)),
				marginPaperUnits: 10.0);

			string content = page.Contents.GetPdfForm(exporter.Configuration);

			Assert.Contains(exporter.Configuration.PageRenderLogs, r => r.LayoutName == "Focused");
			Assert.Contains(exporter.Configuration.LastRenderLog.Entries, e => e.Handle == inside.Handle && e.Status == ACadSharp.Pdf.Core.Render.RenderStatus.Rendered);
			Assert.DoesNotContain(exporter.Configuration.LastRenderLog.Entries, e => e.Handle == outside.Handle && e.Status == ACadSharp.Pdf.Core.Render.RenderStatus.Rendered);
			Assert.Contains("W n", content);
		}

		[Fact]
		public void AddModelWindow_FitsWideWindowIntoViewport()
		{
			var doc = new CadDocument();
			doc.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(100, 0, 0) });

			using var stream = new MemoryStream();
			var exporter = new PdfExporter(stream);
			var layout = new Layout("Wide")
			{
				PaperUnits = PlotPaperUnits.Millimeters,
				PaperWidth = 200,
				PaperHeight = 100,
				DenominatorScale = 1.0,
			};

			PdfPage page = exporter.AddModelWindow(
				doc,
				layout,
				new BoundingBox(new XYZ(0, 0, 0), new XYZ(400, 100, 0)),
				marginPaperUnits: 10.0);

			Viewport viewport = page.Viewports.Single();
			Assert.Equal(180.0, viewport.Width, 6);
			Assert.Equal(80.0, viewport.Height, 6);
			Assert.Equal(177.77777777777777, viewport.ViewHeight, 6);
		}

		[Fact]
		public void AddModelWindow_CullsInsertChildrenOutsideFocusedWindow()
		{
			var doc = new CadDocument();
			var block = new BlockRecord("FOCUS-BLOCK");
			var inside = new Line
			{
				StartPoint = new XYZ(10, 10, 0),
				EndPoint = new XYZ(20, 10, 0),
				Layer = new Layer("0") { Color = Color.Red },
			};
			var farAway = new Line
			{
				StartPoint = new XYZ(1000, 1000, 0),
				EndPoint = new XYZ(1200, 1000, 0),
				Layer = new Layer("0") { Color = Color.Blue },
			};
			block.Entities.Add(inside);
			block.Entities.Add(farAway);
			doc.BlockRecords.Add(block);
			doc.Entities.Add(new Insert(block)
			{
				InsertPoint = XYZ.Zero,
				Layer = new Layer("0") { Color = new Color(255, 255, 255) },
			});

			using var stream = new MemoryStream();
			var exporter = new PdfExporter(stream);
			exporter.Configuration.UseSceneGraph = true;

			var layout = new Layout("FocusedInsert")
			{
				PaperUnits = PlotPaperUnits.Millimeters,
				PaperWidth = 200,
				PaperHeight = 100,
				DenominatorScale = 1.0,
			};

			PdfPage page = exporter.AddModelWindow(
				doc,
				layout,
				new BoundingBox(new XYZ(0, 0, 0), new XYZ(100, 100, 0)),
				marginPaperUnits: 10.0);
			page.Contents.GetPdfForm(exporter.Configuration);

			Assert.Equal(1, exporter.Configuration.LastRenderLog.Entries.Count(e => e.EntityType == "AcDbLine" && e.Status == ACadSharp.Pdf.Core.Render.RenderStatus.Rendered));
			Assert.DoesNotContain(exporter.Configuration.LastRenderLog.Entries, e => e.Handle == farAway.Handle && e.Status == ACadSharp.Pdf.Core.Render.RenderStatus.Rendered);
		}

		[Fact]
		public void AddModelWindow_RendersCirclesWithCubicCurves()
		{
			var doc = new CadDocument();
			doc.Entities.Add(new Circle
			{
				Center = new XYZ(50, 50, 0),
				Radius = 10,
				Layer = new Layer("0") { Color = Color.Red },
			});

			using var stream = new MemoryStream();
			var exporter = new PdfExporter(stream);
			exporter.Configuration.UseSceneGraph = true;

			var layout = new Layout("CircleFocused")
			{
				PaperUnits = PlotPaperUnits.Millimeters,
				PaperWidth = 200,
				PaperHeight = 100,
				DenominatorScale = 1.0,
			};

			PdfPage page = exporter.AddModelWindow(
				doc,
				layout,
				new BoundingBox(new XYZ(0, 0, 0), new XYZ(100, 100, 0)),
				marginPaperUnits: 10.0);
			string content = page.Contents.GetPdfForm(exporter.Configuration);

			Assert.Contains(" c\n", content);
		}

		[Fact]
		public void Export_WritesCompressedContentStreamWithDeclaredFilter()
		{
			var doc = new CadDocument();
			doc.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(100, 0, 0) });

			using var stream = new MemoryStream();
			var exporter = new PdfExporter(stream);
			exporter.Configuration.UseSceneGraph = true;
			exporter.Configuration.CompressContentStreams = true;

			var layout = new Layout("Compressed")
			{
				PaperUnits = PlotPaperUnits.Millimeters,
				PaperWidth = 200,
				PaperHeight = 100,
				DenominatorScale = 1.0,
			};

			exporter.AddModelWindow(
				doc,
				layout,
				new BoundingBox(new XYZ(0, 0, 0), new XYZ(100, 100, 0)),
				marginPaperUnits: 10.0);
			exporter.Close();

			byte[] pdfBytes = stream.ToArray();
			string pdfText = Encoding.ASCII.GetString(pdfBytes);

			Assert.Contains("/Filter /FlateDecode", pdfText);
			byte[] streamPayload = extractFirstStreamPayload(pdfBytes);
			string decompressed = decompress(streamPayload);
			Assert.Contains("W n", decompressed);
		}

		[Fact]
		public void Export_DeclaresPageFontResourcesForTextCommands()
		{
			var doc = new CadDocument();
			doc.Entities.Add(new TextEntity
			{
				Height = 2.5,
				Value = "123.45",
				InsertPoint = new XYZ(10, 10, 0),
				Layer = new Layer("0") { Color = Color.Red },
			});

			using var stream = new MemoryStream();
			var exporter = new PdfExporter(stream);
			exporter.Configuration.UseSceneGraph = true;

			var layout = new Layout("Text")
			{
				PaperUnits = PlotPaperUnits.Millimeters,
				PaperWidth = 200,
				PaperHeight = 100,
				DenominatorScale = 1.0,
			};

			exporter.AddModelWindow(
				doc,
				layout,
				new BoundingBox(new XYZ(0, 0, 0), new XYZ(100, 100, 0)),
				marginPaperUnits: 10.0);
			exporter.Close();

			string pdfText = Encoding.ASCII.GetString(stream.ToArray());
			Assert.Contains("/Resources <<", pdfText);
			Assert.Contains("/Font <<", pdfText);
			Assert.Contains("/F1 ", pdfText);
			Assert.Contains("/BaseFont /Helvetica", pdfText);
		}

		private static byte[] extractFirstStreamPayload(byte[] pdfBytes)
		{
			byte[] streamMarker = Encoding.ASCII.GetBytes("stream\n");
			byte[] endStreamMarker = Encoding.ASCII.GetBytes("\nendstream");

			int streamIndex = indexOf(pdfBytes, streamMarker, 0);
			Assert.True(streamIndex >= 0, "PDF stream marker was not found.");
			int payloadStart = streamIndex + streamMarker.Length;
			int endStreamIndex = indexOf(pdfBytes, endStreamMarker, payloadStart);
			Assert.True(endStreamIndex >= 0, "PDF endstream marker was not found.");

			int length = endStreamIndex - payloadStart;
			byte[] payload = new byte[length];
			Buffer.BlockCopy(pdfBytes, payloadStart, payload, 0, length);
			return payload;
		}

		private static int indexOf(byte[] haystack, byte[] needle, int startIndex)
		{
			for (int i = startIndex; i <= haystack.Length - needle.Length; i++)
			{
				bool match = true;
				for (int j = 0; j < needle.Length; j++)
				{
					if (haystack[i + j] != needle[j])
					{
						match = false;
						break;
					}
				}

				if (match)
				{
					return i;
				}
			}

			return -1;
		}

		private static string decompress(byte[] compressed)
		{
			using var input = new MemoryStream(compressed);
			using var deflate = new DeflateStream(input, CompressionMode.Decompress);
			using var output = new MemoryStream();
			deflate.CopyTo(output);
			return Encoding.ASCII.GetString(output.ToArray());
		}
	}
}
