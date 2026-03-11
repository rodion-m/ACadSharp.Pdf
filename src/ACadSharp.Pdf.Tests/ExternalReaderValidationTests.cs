using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Pdf.Core;
using ACadSharp.Tables;
using CSMath;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace ACadSharp.Pdf.Tests
{
	public class ExternalReaderValidationTests
	{
		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void FullPageExports_OpenInExternalReaders_AndExposeVisibleText(bool useSceneGraph)
		{
			FileInfo file = exportSimpleDocument(
				useSceneGraph,
				$"fullpage_{(useSceneGraph ? "scenegraph" : "legacy")}.pdf",
				addFocusedWindow: false);

			ExternalPdfAssertions.AssertCanOpenAndRasterize(file);
			ExternalPdfAssertions.AssertContainsVisibleText(file, "VISIBLE_LABEL_123");
		}

		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void FocusedWindowExports_OpenInExternalReaders_AndExposeVisibleText(bool useSceneGraph)
		{
			FileInfo file = exportSimpleDocument(
				useSceneGraph,
				$"focused_{(useSceneGraph ? "scenegraph" : "legacy")}.pdf",
				addFocusedWindow: true);

			ExternalPdfAssertions.AssertCanOpenAndRasterize(file);
			ExternalPdfAssertions.AssertContainsVisibleText(file, "VISIBLE_LABEL_123");

			byte[] pdfBytes = File.ReadAllBytes(file.FullName);
			Assert.Contains("/Filter /FlateDecode", Encoding.ASCII.GetString(pdfBytes));
			Assert.Equal(0x78, extractFirstStreamPayload(pdfBytes)[0]);
		}

		private static FileInfo exportSimpleDocument(bool useSceneGraph, string fileName, bool addFocusedWindow)
		{
			string directory = Path.Combine(Path.GetTempPath(), "acadsharp-pdf-tests");
			Directory.CreateDirectory(directory);
			string path = Path.Combine(directory, $"{Guid.NewGuid():N}_{fileName}");

			var doc = new CadDocument();
			doc.Entities.Add(new Line
			{
				StartPoint = XYZ.Zero,
				EndPoint = new XYZ(100, 0, 0),
				Layer = new Layer("0") { Color = Color.Red },
			});
			doc.Entities.Add(new TextEntity
			{
				Value = "VISIBLE_LABEL_123",
				Height = 5.0,
				InsertPoint = new XYZ(10, 10, 0),
				Layer = new Layer("0") { Color = Color.Blue },
			});

			PdfExporter exporter = new PdfExporter(path);
			exporter.Configuration.UseSceneGraph = useSceneGraph;
			exporter.Configuration.CompressContentStreams = true;

			if (addFocusedWindow)
			{
				Layout layout = new Layout("Focused")
				{
					PaperUnits = PlotPaperUnits.Millimeters,
					PaperWidth = 200.0,
					PaperHeight = 100.0,
					DenominatorScale = 1.0,
				};

				exporter.AddModelWindow(
					doc,
					layout,
					new BoundingBox(new XYZ(0, 0, 0), new XYZ(120, 40, 0)),
					marginPaperUnits: 10.0);
			}
			else
			{
				exporter.AddModelSpace(doc);
			}

			exporter.Close();
			return new FileInfo(path);
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
	}
}
