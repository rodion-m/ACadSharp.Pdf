using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Pdf.Core;
using ACadSharp.Tables;
using CSMath;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ACadSharp.Pdf
{
	/// <summary>
	/// Exporter to create a pdf document.
	/// </summary>
	public class PdfExporter
	{
		/// <summary>
		/// Configuration for the <see cref="PdfExporter"/> instance.
		/// </summary>
		public PdfConfiguration Configuration { get; } = new PdfConfiguration();

		private readonly PdfDocument _pdf;
		private readonly Stream _stream;

		/// <summary>
		/// Initialize an instance of <see cref="PdfExporter"/>.
		/// </summary>
		/// <param name="path">Path where the pdf will be saved.</param>
		public PdfExporter(string path) : this(File.Create(path))
		{
		}

		/// <summary>
		/// Initialize an instance of <see cref="PdfExporter"/>.
		/// </summary>
		/// <param name="stream">Stream where the pdf will be saved.</param>
		public PdfExporter(Stream stream)
		{
			this._stream = stream;
			this._pdf = new PdfDocument();
		}

		/// <summary>
		/// Add the model space from a cad document.
		/// </summary>
		/// <param name="document"></param>
		/// <remarks>
		/// This method does not import the <see cref="TableEntry"/> from the document.
		/// </remarks>
		public void AddModelSpace(CadDocument document)
		{
			this.Add(document.ModelSpace);
		}

		/// <summary>
		/// Add all the paper layouts 
		/// </summary>
		public void AddPaperLayouts(CadDocument document)
		{
			this.Add(document.Layouts);
		}

		/// <summary>
		/// Add layouts to the pdf as pages.
		/// </summary>
		/// <param name="layouts"></param>
		public void Add(IEnumerable<Layout> layouts)
		{
			foreach (var layout in layouts)
			{
				if (!layout.IsPaperSpace)
				{
					continue;
				}

				this.Add(layout);
			}
		}

		/// <summary>
		/// Add a <see cref="Layout"/> to the pdf as a page.
		/// </summary>
		/// <param name="layout"></param>
		public void Add(Layout layout)
		{
			PdfPage page = this._pdf.Pages.AddPage();

			page.Layout = layout;

			foreach (Entity e in layout.AssociatedBlock.Entities)
			{
				if (e is Viewport)
				{
					continue;
				}

				page.Entities.Add(e);
			}

			foreach (Viewport vp in layout.Viewports)
			{
				if (vp.RepresentsPaper)
				{
					continue;
				}

				page.Viewports.Add(vp);
			}
		}

		/// <summary>
		/// Add a <see cref="BlockRecord"/> as a page.
		/// </summary>
		/// <param name="block"></param>
		public void Add(BlockRecord block)
		{
			PdfPage page = this._pdf.Pages.AddPage();

			page.Add(block);
		}

		/// <summary>
		/// Add a <see cref="BlockRecord"/> as a page using a caller-provided layout.
		/// </summary>
		/// <param name="block">Block to draw.</param>
		/// <param name="layout">Layout applied to the page.</param>
		/// <param name="resizeLayout">Resize the layout to fit the entities.</param>
		/// <returns>The created <see cref="PdfPage"/>.</returns>
		public PdfPage Add(BlockRecord block, Layout layout, bool resizeLayout)
		{
			PdfPage page = this._pdf.Pages.AddPage();
			page.Layout = layout;
			page.Add(block, resizeLayout);
			return page;
		}

		/// <summary>
		/// Add a focused model-space window as a page using a synthetic viewport.
		/// </summary>
		/// <param name="document">Source drawing.</param>
		/// <param name="layout">Paper layout used for the page.</param>
		/// <param name="modelWindow">Finite model-space bounds to render.</param>
		/// <param name="marginPaperUnits">Additional page margin in the layout paper units.</param>
		/// <returns>The created <see cref="PdfPage"/>.</returns>
		public PdfPage AddModelWindow(CadDocument document, Layout layout, BoundingBox modelWindow, double marginPaperUnits = 0.0)
		{
			if (document == null)
			{
				throw new ArgumentNullException(nameof(document));
			}

			if (layout == null)
			{
				throw new ArgumentNullException(nameof(layout));
			}

			if (modelWindow.Extent != BoundingBoxExtent.Finite && modelWindow.Extent != BoundingBoxExtent.Point)
			{
				throw new ArgumentException("Model window must be finite.", nameof(modelWindow));
			}

			double rawWidth = Math.Max(0.0, modelWindow.Max.X - modelWindow.Min.X);
			double rawHeight = Math.Max(0.0, modelWindow.Max.Y - modelWindow.Min.Y);
			if (rawWidth <= 1e-9 && rawHeight <= 1e-9)
			{
				throw new ArgumentException("Model window must have a measurable size.", nameof(modelWindow));
			}

			double availableWidth = Math.Max(1e-6, layout.PaperWidth - (2.0 * marginPaperUnits));
			double availableHeight = Math.Max(1e-6, layout.PaperHeight - (2.0 * marginPaperUnits));
			double sourceAspect = rawWidth <= 1e-9 ? 1.0 : rawWidth / Math.Max(rawHeight, 1e-9);
			double targetAspect = availableWidth / availableHeight;

			double fittedViewHeight;
			if (rawWidth <= 1e-9)
			{
				fittedViewHeight = rawHeight;
			}
			else if (sourceAspect > targetAspect)
			{
				fittedViewHeight = rawWidth / targetAspect;
			}
			else
			{
				fittedViewHeight = Math.Max(rawHeight, rawWidth / targetAspect);
			}

			var viewport = new Viewport
			{
				Center = new XYZ(marginPaperUnits + (availableWidth / 2.0), marginPaperUnits + (availableHeight / 2.0), 0.0),
				Width = availableWidth,
				Height = availableHeight,
				ViewCenter = new XY((modelWindow.Min.X + modelWindow.Max.X) / 2.0, (modelWindow.Min.Y + modelWindow.Max.Y) / 2.0),
				ViewHeight = Math.Max(fittedViewHeight, 1e-6),
				ViewDirection = XYZ.AxisZ,
			};

			PdfPage page = this._pdf.Pages.AddPage();
			page.Layout = layout;
			page.ModelEntities.AddRange(document.ModelSpace.Entities.Where(e => e != null));
			page.Viewports.Add(viewport);
			return page;
		}

		/// <summary>
		/// Close the document and save it.
		/// </summary>
		public void Close()
		{
			using (PdfWriter writer = new PdfWriter(this._stream, this._pdf, this.Configuration))
			{
				writer.Write();
			}
		}
	}
}
