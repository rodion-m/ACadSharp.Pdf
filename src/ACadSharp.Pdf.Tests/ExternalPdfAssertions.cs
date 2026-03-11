using Docnet.Core;
using Docnet.Core.Models;
using System.IO;
using System.Linq;
using Xunit;

namespace ACadSharp.Pdf.Tests
{
	internal static class ExternalPdfAssertions
	{
		public static void AssertCanOpenAndRasterize(FileInfo file)
		{
			Assert.True(file.Exists, $"PDF not created: {file.FullName}");
			Assert.True(file.Length > 0, $"PDF is empty: {file.FullName}");

			using var docReader = DocLib.Instance.GetDocReader(file.FullName, new PageDimensions(1.5));
			Assert.True(docReader.GetPageCount() > 0, $"PDF contains no pages: {file.FullName}");

			using var pageReader = docReader.GetPageReader(0);
			int width = pageReader.GetPageWidth();
			int height = pageReader.GetPageHeight();
			byte[] bgra = pageReader.GetImage();

			Assert.True(width > 0, $"Rasterized width must be positive: {file.FullName}");
			Assert.True(height > 0, $"Rasterized height must be positive: {file.FullName}");
			Assert.NotNull(bgra);
			Assert.True(bgra.Length >= width * height * 4, $"Rasterized pixel buffer is incomplete: {file.FullName}");
		}

		public static void AssertContainsVisibleText(FileInfo file, string expectedText)
		{
			using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(file.FullName);
			string text = string.Concat(document.GetPages().Select(p => p.Text));
			Assert.Contains(expectedText, text);
		}
	}
}
