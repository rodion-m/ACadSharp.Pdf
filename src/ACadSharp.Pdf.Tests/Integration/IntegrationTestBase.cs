using ACadSharp.Entities;
using ACadSharp.IO;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace ACadSharp.Pdf.Tests.Integration
{
	public abstract class IntegrationTestBase
	{
		protected static readonly string FixturesFolder =
			Path.Combine(TestVariables.SamplesFolder, "fixtures");

		protected static readonly string OutputFolder =
			Path.Combine(TestVariables.OutputSamplesFolder, "integration");

		protected readonly ITestOutputHelper _output;

		protected IntegrationTestBase(ITestOutputHelper output)
		{
			this._output = output;

			if (!Directory.Exists(OutputFolder))
			{
				Directory.CreateDirectory(OutputFolder);
			}
		}

		protected CadDocument LoadFixture(string filename)
		{
			string path = Path.Combine(FixturesFolder, filename);
			Assert.True(File.Exists(path), $"Fixture not found: {path}");
			return DxfReader.Read(path);
		}

		protected FileInfo ExportLegacy(CadDocument doc, string name, string basePath = null)
		{
			string path = Path.Combine(OutputFolder, $"{name}_legacy.pdf");
			PdfExporter exporter = new PdfExporter(path);
			exporter.Configuration.UseSceneGraph = false;
			if (!string.IsNullOrWhiteSpace(basePath))
			{
				exporter.Configuration.BasePath = basePath;
			}
			exporter.Configuration.OnNotification += this.onNotification;
			exporter.AddModelSpace(doc);
			exporter.Close();
			return new FileInfo(path);
		}

		protected FileInfo ExportSceneGraph(CadDocument doc, string name, string basePath = null)
		{
			string path = Path.Combine(OutputFolder, $"{name}_scenegraph.pdf");
			PdfExporter exporter = new PdfExporter(path);
			exporter.Configuration.UseSceneGraph = true;
			if (!string.IsNullOrWhiteSpace(basePath))
			{
				exporter.Configuration.BasePath = basePath;
			}
			exporter.Configuration.OnNotification += this.onNotification;
			exporter.AddModelSpace(doc);
			exporter.Close();
			return new FileInfo(path);
		}

		protected void AssertValidPdf(FileInfo file)
		{
			Assert.True(file.Exists, $"PDF not created: {file.FullName}");
			Assert.True(file.Length > 0, $"PDF is empty: {file.FullName}");

			using (FileStream fs = file.OpenRead())
			{
				byte[] header = new byte[5];
				int bytesRead = fs.Read(header, 0, 5);
				Assert.Equal(5, bytesRead);
				string headerStr = System.Text.Encoding.ASCII.GetString(header);
				Assert.Equal("%PDF-", headerStr);
			}
		}

		protected int CountEntities<T>(CadDocument doc) where T : Entity
		{
			return doc.ModelSpace.Entities.OfType<T>().Count();
		}

		protected static string ReadPdfAscii(FileInfo file)
		{
			byte[] bytes = File.ReadAllBytes(file.FullName);
			return Encoding.ASCII.GetString(bytes);
		}

		protected static string ReadPdfDecodedContent(FileInfo file)
		{
			byte[] pdfBytes = File.ReadAllBytes(file.FullName);
			StringBuilder sb = new StringBuilder();
			int scanIndex = 0;

			while (tryReadNextStream(pdfBytes, ref scanIndex, out bool isFlate, out byte[] payload))
			{
				byte[] decoded = isFlate ? decompress(payload) : payload;
				sb.Append(Encoding.GetEncoding(28591).GetString(decoded));
			}

			return sb.ToString();
		}

		private void onNotification(object sender, NotificationEventArgs e)
		{
			this._output.WriteLine(e.Message);
		}

		private static bool tryReadNextStream(byte[] pdfBytes, ref int scanIndex, out bool isFlate, out byte[] payload)
		{
			isFlate = false;
			payload = null;

			byte[] streamMarker = Encoding.ASCII.GetBytes("stream\n");
			byte[] endStreamMarker = Encoding.ASCII.GetBytes("\nendstream");

			int streamIndex = indexOf(pdfBytes, streamMarker, scanIndex);
			if (streamIndex < 0)
			{
				return false;
			}

			int payloadStart = streamIndex + streamMarker.Length;
			int endStreamIndex = indexOf(pdfBytes, endStreamMarker, payloadStart);
			if (endStreamIndex < 0)
			{
				return false;
			}

			int dictProbeStart = System.Math.Max(0, streamIndex - 1024);
			string nearbyHeader = Encoding.ASCII.GetString(pdfBytes, dictProbeStart, streamIndex - dictProbeStart);
			isFlate = nearbyHeader.Contains("/Filter /FlateDecode");

			int length = endStreamIndex - payloadStart;
			payload = new byte[length];
			System.Buffer.BlockCopy(pdfBytes, payloadStart, payload, 0, length);
			scanIndex = endStreamIndex + endStreamMarker.Length;
			return true;
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

		private static byte[] decompress(byte[] compressed)
		{
			return PdfStreamDecoding.DecodeFlatePayload(compressed);
		}
	}
}
