using System.Collections.Generic;
using System.Text;

namespace ACadSharp.Pdf.Core
{
	public class PdfInlineDictionary : PdfItem
	{
		public Dictionary<string, PdfItem> Items { get; } = new();

		public override string GetPdfForm(PdfConfiguration configuration)
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("<<");
			foreach (var item in this.Items)
			{
				sb.AppendLine($"{item.Key} {item.Value.GetPdfForm(configuration)}");
			}
			sb.Append(">>");
			return sb.ToString();
		}
	}
}
