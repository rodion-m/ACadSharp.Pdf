using ACadSharp.Entities;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace ACadSharp.Pdf.Tests.Integration
{
	public class Stage06ImageUnderlayIntegrationTests : IntegrationTestBase
	{
		private const string Fixture = "stage06_image_underlay.dxf";

		public Stage06ImageUnderlayIntegrationTests(ITestOutputHelper output) : base(output) { }

		[Fact(Skip = "Stage 06 (Image/Underlay) rendering not yet implemented; external files missing")]
		public void LoadFixture_HasImageAndUnderlay()
		{
			CadDocument doc = this.LoadFixture(Fixture);

			int imageCount = this.CountEntities<RasterImage>(doc);
			int underlayCount = this.CountEntities<PdfUnderlay>(doc);

			Assert.True(imageCount >= 1, $"Expected at least 1 RasterImage, found {imageCount}");
			Assert.True(underlayCount >= 1, $"Expected at least 1 PdfUnderlay, found {underlayCount}");
		}

		[Fact(Skip = "Stage 06 (Image/Underlay) rendering not yet implemented")]
		public void ExportLegacy_ProducesValidPdf()
		{
			CadDocument doc = this.LoadFixture(Fixture);
			FileInfo pdf = this.ExportLegacy(doc, "stage06");
			this.AssertValidPdf(pdf);
		}

		[Fact(Skip = "Stage 06 (Image/Underlay) rendering not yet implemented")]
		public void ExportSceneGraph_ProducesValidPdf()
		{
			CadDocument doc = this.LoadFixture(Fixture);
			FileInfo pdf = this.ExportSceneGraph(doc, "stage06");
			this.AssertValidPdf(pdf);
		}
	}
}
