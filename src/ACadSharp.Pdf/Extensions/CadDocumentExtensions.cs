using ACadSharp.IO;
using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;
using System;
using System.IO;

namespace ACadSharp.Pdf.Extensions
{
	public static class CadDocumentExtensions
	{
		/// <summary>
		/// Create a <see cref="DwgPreview"/> with a png image in it using the model space as a reference.
		/// </summary>
		public static DwgPreview CreatePreview(this CadDocument document)
		{
			if (document == null)
			{
				throw new ArgumentNullException(nameof(document));
			}

			try
			{
				return createPdfBackedPreview(document);
			}
			catch
			{
				return createPlaceholderPreview();
			}
		}

		private static DwgPreview createPdfBackedPreview(CadDocument document)
		{
			byte[] pdfBytes;
			using (MemoryStream pdfStream = new MemoryStream())
			{
				PdfExporter exporter = new PdfExporter(pdfStream);
				exporter.Configuration.UseSceneGraph = true;
				exporter.AddModelSpace(document);
				exporter.Close();
				pdfBytes = pdfStream.ToArray();
			}

			if (pdfBytes == null || pdfBytes.Length == 0)
			{
				throw new InvalidOperationException("Unable to create preview: PDF export produced no bytes.");
			}

			string tempPdf = Path.Combine(Path.GetTempPath(), $"acadsharp-preview-{Guid.NewGuid():N}.pdf");
			File.WriteAllBytes(tempPdf, pdfBytes);

			try
			{
				using var docReader = DocLib.Instance.GetDocReader(tempPdf, new PageDimensions(1.5));
				if (docReader.GetPageCount() <= 0)
				{
					throw new InvalidOperationException("Unable to create preview: generated PDF has no pages.");
				}

				using var pageReader = docReader.GetPageReader(0);
				int width = pageReader.GetPageWidth();
				int height = pageReader.GetPageHeight();
				byte[] bgra = pageReader.GetImage();

				if (width <= 0 || height <= 0 || bgra == null || bgra.Length < width * height * 4)
				{
					throw new InvalidOperationException("Unable to create preview: PDF rasterizer returned an invalid pixel buffer.");
				}

				using SKData raster = SKData.CreateCopy(bgra);
				using SKImage image = SKImage.FromPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), raster, width * 4);
				if (image == null)
				{
					throw new InvalidOperationException("Unable to create preview: raster image could not be materialized.");
				}

				using SKData png = image.Encode(SKEncodedImageFormat.Png, quality: 100);
				if (png == null || png.Size == 0)
				{
					throw new InvalidOperationException("Unable to create preview: PNG encoding failed.");
				}

				return new DwgPreview(DwgPreview.PreviewType.Png, new byte[80], png.ToArray());
			}
			finally
			{
				try
				{
					if (File.Exists(tempPdf))
					{
						File.Delete(tempPdf);
					}
				}
				catch
				{
				}
			}
		}

		private static DwgPreview createPlaceholderPreview()
		{
			using SKSurface surface = SKSurface.Create(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul));
			SKCanvas canvas = surface.Canvas;
			canvas.Clear(new SKColor(245, 245, 245));

			using (SKPaint stroke = new SKPaint
			{
				Style = SKPaintStyle.Stroke,
				Color = new SKColor(64, 64, 64),
				StrokeWidth = 4,
				IsAntialias = true,
			})
			{
				canvas.DrawRect(new SKRect(16, 16, 240, 240), stroke);
				canvas.DrawLine(16, 16, 240, 240, stroke);
				canvas.DrawLine(240, 16, 16, 240, stroke);
			}

			using SKImage image = surface.Snapshot();
			using SKData data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
			return new DwgPreview(DwgPreview.PreviewType.Png, new byte[80], data.ToArray());
		}
	}
}
