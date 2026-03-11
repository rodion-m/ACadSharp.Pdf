namespace ACadSharp.Pdf.Core
{
	public class PdfStandardFont : PdfDictionary
	{
		public PdfStandardFont(string baseFontName = "/Helvetica")
		{
			this.Items.Add("/Type", new PdfName("/Font"));
			this.Items.Add("/Subtype", new PdfName("/Type1"));
			this.Items.Add("/BaseFont", new PdfName(baseFontName));
			this.Items.Add("/Encoding", new PdfName("/WinAnsiEncoding"));
		}
	}
}
