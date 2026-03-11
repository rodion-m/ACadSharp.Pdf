using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.IO;
using ACadSharp.Pdf.Core;
using ACadSharp.Objects;
using ACadSharp.Pdf.Core.Render;
using ACadSharp.Pdf.Core.Render.Transforms;
using ACadSharp.Pdf.Verification;
using CSMath;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ACadSharp.Pdf.Examples
{
	internal class Program
	{
		static void Main(string[] args)
		{
			Options options = Options.Parse(args);
			var notifications = new NotificationCollector();
			CadDocument doc = loadDocument(options.InputPath, notifications);

			string outputPdf = options.OutputPdfPath;
			Directory.CreateDirectory(Path.GetDirectoryName(outputPdf) ?? ".");

			PdfExporter exporter = new PdfExporter(outputPdf);
			exporter.Configuration.UseSceneGraph = options.UseSceneGraph;
			exporter.Configuration.BasePath = Path.GetDirectoryName(options.InputPath) ?? string.Empty;
			exporter.Configuration.OnNotification += notifications.OnNotification;

			PreviewLayoutPlan previewPlan = null;
			if (options.ExportModelSpace)
			{
				previewPlan = addModelSpace(exporter, doc, options);
			}

			if (options.ExportPaperLayouts)
			{
				if (options.VerificationMode == VerificationMode.PublicationSheet)
				{
					addPublicationSheet(exporter, doc);
				}
				else
				{
					exporter.AddPaperLayouts(doc);
				}
			}

			exporter.Close();

			string reportPath = options.ReportPath ?? Path.ChangeExtension(outputPdf, ".render-report.json");
			writeReport(reportPath, doc, options, previewPlan, exporter.Configuration.PageRenderLogs, notifications);
			notifications.FlushToConsole();

			Console.WriteLine($"PDF: {outputPdf}");
			Console.WriteLine($"Report: {reportPath}");
		}

		private static PreviewLayoutPlan addModelSpace(PdfExporter exporter, CadDocument doc, Options options)
		{
			if (!options.FitModelToPaper)
			{
				exporter.AddModelSpace(doc);
				return null;
			}

			BoundingBox limits;
			PreviewExtentsSelection extentsSelection;
			if (options.TryGetWindow(out BoundingBox explicitWindow))
			{
				limits = explicitWindow;
				extentsSelection = new PreviewExtentsSelection(
					explicitWindow,
					"explicit-window",
					0,
					0,
					Array.Empty<string>(),
					0,
					1,
					0.0,
					Array.Empty<PreviewExtentsExclusion>());
			}
			else if (!PreviewExtentsSelector.TrySelect(doc.ModelSpace.Entities, options.FocusHandles, options.FocusPaddingModelUnits, out extentsSelection))
			{
				exporter.AddModelSpace(doc);
				return null;
			}
			else
			{
				limits = extentsSelection.Limits;
			}

			var layout = new Layout("ModelPreview")
			{
				PaperUnits = PlotPaperUnits.Millimeters,
				PaperWidth = options.PaperWidthMm,
				PaperHeight = options.PaperHeightMm,
				DenominatorScale = 1.0,
				PaperRotation = PlotRotation.NoRotation,
			};

			PdfPage page = exporter.AddModelWindow(doc, layout, limits, options.MarginMm);
			if (extentsSelection?.IncludedHandles?.Count > 0)
			{
				HashSet<string> includedHandles = new HashSet<string>(extentsSelection.IncludedHandles, StringComparer.OrdinalIgnoreCase);
				page.ModelEntities.Clear();
				page.ModelEntities.AddRange(doc.ModelSpace.Entities.Where(e => e != null && includedHandles.Contains(e.Handle.ToString("X"))));
			}

			Viewport previewViewport = page.Viewports.Single();

			return new PreviewLayoutPlan
			{
				PaperWidthMm = options.PaperWidthMm,
				PaperHeightMm = options.PaperHeightMm,
				MarginMm = options.MarginMm,
				FocusHandles = options.FocusHandles.ToArray(),
				FocusPaddingModelUnits = options.FocusPaddingModelUnits,
				ExtentsSelection = createPreviewSelectionSummary(extentsSelection),
				DenominatorScale = previewViewport.ViewHeight / previewViewport.Height,
				MinX = limits.Min.X,
				MinY = limits.Min.Y,
				MaxX = limits.Max.X,
				MaxY = limits.Max.Y,
				TranslationX = 0.0,
				TranslationY = 0.0,
				ViewportWidth = previewViewport.Width,
				ViewportHeight = previewViewport.Height,
				ViewHeight = previewViewport.ViewHeight,
			};
		}

		private static void addPublicationSheet(PdfExporter exporter, CadDocument doc)
		{
			Layout layout = doc.Layouts.FirstOrDefault(l => l.IsPaperSpace);
			if (layout == null)
			{
				return;
			}

			List<Viewport> publicationViewports = layout.Viewports
				.Where(vp => vp != null && !vp.RepresentsPaper)
				.ToList();
			if (publicationViewports.Count == 0)
			{
				exporter.Add(layout);
				return;
			}

			Viewport mainViewport = publicationViewports
				.OrderByDescending(vp => estimateViewportModelCoverage(doc.ModelSpace.Entities, vp))
				.ThenByDescending(vp => vp.Width * vp.Height)
				.First();

			BoundingBox modelWindow = TransformHelper.GetViewportModelBoundingBox(mainViewport);
			Layout sheetLayout = layout;

			double pageWidth = rotatedPaperWidth(sheetLayout);
			double pageHeight = rotatedPaperHeight(sheetLayout);
			double margin = 10.0;
			double availableHeight = Math.Max(40.0, pageHeight - (2.0 * margin));
			double availableWidth = Math.Max(40.0, pageWidth - (2.0 * margin));

			PdfPage page = exporter.AddModelWindow(
				doc,
				sheetLayout,
				modelWindow,
				marginPaperUnits: 0.0);

			Viewport syntheticViewport = page.Viewports.Single();
			syntheticViewport.Center = new XYZ(pageWidth / 2.0, pageHeight / 2.0, 0.0);
			syntheticViewport.Width = availableWidth;
			syntheticViewport.Height = availableHeight;
		}

		private static int estimateViewportModelCoverage(IEnumerable<Entity> entities, Viewport viewport)
		{
			if (entities == null || viewport == null)
			{
				return 0;
			}

			BoundingBox box = TransformHelper.GetViewportModelBoundingBox(viewport);
			int count = 0;
			foreach (Entity entity in entities)
			{
				if (entity == null)
				{
					continue;
				}

				BoundingBox entityBox = entity.GetBoundingBox();
				if (entityBox.Extent == BoundingBoxExtent.Infinite)
				{
					if (entity is Insert)
					{
						count++;
					}
					continue;
				}

				if (box.IsIn(entityBox, out bool partialIn) || partialIn)
				{
					count++;
				}
			}

			return count;
		}

		private static double rotatedPaperWidth(Layout layout)
		{
			return layout.PaperRotation == PlotRotation.Degrees90 || layout.PaperRotation == PlotRotation.Degrees270
				? layout.PaperHeight
				: layout.PaperWidth;
		}

		private static double rotatedPaperHeight(Layout layout)
		{
			return layout.PaperRotation == PlotRotation.Degrees90 || layout.PaperRotation == PlotRotation.Degrees270
				? layout.PaperWidth
				: layout.PaperHeight;
		}

		private static PreviewExtentsSelectionSummary createPreviewSelectionSummary(PreviewExtentsSelection selection)
		{
			if (selection == null)
			{
				return null;
			}

			return new PreviewExtentsSelectionSummary
			{
				Strategy = selection.Strategy,
				CandidateCount = selection.CandidateCount,
				IncludedCount = selection.IncludedCount,
				FilteredByHandleCount = selection.FilteredByHandleCount,
				ClusterCount = selection.ClusterCount,
				ConnectionDistance = selection.ConnectionDistance,
				ExcludedEntities = selection.ExcludedEntities
					.Select(e => new PreviewExtentsExclusionSummary
					{
						Handle = e.Handle,
						EntityType = e.EntityType,
						Reason = e.Reason,
						GapDistance = e.GapDistance,
					})
					.ToArray(),
			};
		}

		private static CadDocument loadDocument(string path, NotificationCollector notifications)
		{
			string ext = Path.GetExtension(path).ToLowerInvariant();
			if (ext == ".dxf")
			{
				return DxfReader.Read(path);
			}

			ACadSharp.IO.NotificationEventHandler handler = notifications != null ? new ACadSharp.IO.NotificationEventHandler(notifications.OnNotification) : null;
			return DwgReader.Read(path, handler);
		}

		private static void writeReport(string reportPath, CadDocument doc, Options options, PreviewLayoutPlan previewPlan, IEnumerable<PageRenderLog> pageRenderLogs, NotificationCollector notifications)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");

			var modelCounts = doc.ModelSpace.Entities
				.GroupBy(e => e.GetType().Name)
				.OrderByDescending(g => g.Count())
				.ToDictionary(g => g.Key, g => g.Count());

			var layoutSummaries = doc.Layouts
				.OrderBy(l => l.Name)
				.Select(l => new
				{
					l.Name,
					l.IsPaperSpace,
					EntityCount = l.AssociatedBlock?.Entities?.Count() ?? 0,
					ViewportCount = l.Viewports?.Count() ?? 0,
				})
				.ToList();

			var renderEntries = (pageRenderLogs ?? Array.Empty<PageRenderLog>())
				.SelectMany((pageLog, pageIndex) => (pageLog?.Log?.Entries ?? Array.Empty<RenderLogEntry>())
					.Select(e => new
					{
						pageIndex,
						layout = pageLog?.LayoutName,
						handle = e.Handle.ToString("X"),
						entityType = e.EntityType,
						status = e.Status.ToString(),
						reason = e.Reason,
					}))
				.ToList();

			var renderStatusCounts = renderEntries
				.GroupBy(e => e.status)
				.OrderBy(g => g.Key)
				.ToDictionary(g => g.Key, g => g.Count());

			var renderTypeCounts = renderEntries
				.GroupBy(e => e.entityType)
				.OrderByDescending(g => g.Count())
				.ToDictionary(g => g.Key, g => g.Count());

			var renderPageSummaries = (pageRenderLogs ?? Array.Empty<PageRenderLog>())
				.Select((pageLog, pageIndex) => new
				{
					pageIndex,
					layout = pageLog?.LayoutName,
					entryCount = pageLog?.Log?.Entries?.Count ?? 0,
					statusCounts = (pageLog?.Log?.Entries ?? Array.Empty<RenderLogEntry>())
						.GroupBy(e => e.Status.ToString())
						.OrderBy(g => g.Key)
						.ToDictionary(g => g.Key, g => g.Count()),
				})
				.ToList();

			var report = new
			{
				input = options.InputPath,
				outputPdf = options.OutputPdfPath,
				sceneGraph = options.UseSceneGraph,
				verificationMode = options.GetVerificationModeName(),
				exportModelSpace = options.ExportModelSpace,
				exportPaperLayouts = options.ExportPaperLayouts,
				fitModelToPaper = options.FitModelToPaper,
				previewLayout = previewPlan,
				document = new
				{
					layoutCount = doc.Layouts.Count(),
					paperLayoutCount = doc.Layouts.Count(l => l.IsPaperSpace),
					modelEntityCount = doc.ModelSpace.Entities.Count(),
					modelEntityTypes = modelCounts,
					layouts = layoutSummaries,
				},
				renderLog = new
				{
					entryCount = renderEntries.Count,
					statusCounts = renderStatusCounts,
					entityTypeCounts = renderTypeCounts,
					pages = renderPageSummaries,
					entries = renderEntries,
				},
				notifications = notifications?.CreateSummary(),
			};

			var jsonOptions = new JsonSerializerOptions
			{
				WriteIndented = true,
			};
			File.WriteAllText(reportPath, JsonSerializer.Serialize(report, jsonOptions));
		}

		private sealed class PreviewLayoutPlan
		{
			public double PaperWidthMm { get; set; }
			public double PaperHeightMm { get; set; }
			public double MarginMm { get; set; }
			public string[] FocusHandles { get; set; }
			public double FocusPaddingModelUnits { get; set; }
			public PreviewExtentsSelectionSummary ExtentsSelection { get; set; }
			public double DenominatorScale { get; set; }
			public double MinX { get; set; }
			public double MinY { get; set; }
			public double MaxX { get; set; }
			public double MaxY { get; set; }
			public double TranslationX { get; set; }
			public double TranslationY { get; set; }
			public double ViewportWidth { get; set; }
			public double ViewportHeight { get; set; }
			public double ViewHeight { get; set; }
		}

		private sealed class PreviewExtentsSelectionSummary
		{
			public string Strategy { get; set; }
			public int CandidateCount { get; set; }
			public int IncludedCount { get; set; }
			public int FilteredByHandleCount { get; set; }
			public int ClusterCount { get; set; }
			public double ConnectionDistance { get; set; }
			public PreviewExtentsExclusionSummary[] ExcludedEntities { get; set; }
		}

		private sealed class PreviewExtentsExclusionSummary
		{
			public string Handle { get; set; }
			public string EntityType { get; set; }
			public string Reason { get; set; }
			public double? GapDistance { get; set; }
		}

		private sealed class Options
		{
			private static readonly IReadOnlyDictionary<string, (double WidthMm, double HeightMm)> _paperFormats =
				new Dictionary<string, (double WidthMm, double HeightMm)>(StringComparer.OrdinalIgnoreCase)
				{
					["A0"] = (841.0, 1189.0),
					["A1"] = (594.0, 841.0),
					["A2"] = (420.0, 594.0),
					["A3"] = (297.0, 420.0),
					["A4"] = (210.0, 297.0),
					["A5"] = (148.0, 210.0),
					["A6"] = (105.0, 148.0),
					["A7"] = (74.0, 105.0),
					["A8"] = (52.0, 74.0),
					["A9"] = (37.0, 52.0),
					["A10"] = (26.0, 37.0),
				};

			public string InputPath { get; private set; }
			public string OutputPdfPath { get; private set; }
			public string ReportPath { get; private set; }
			public bool UseSceneGraph { get; private set; } = true;
			public bool ExportModelSpace { get; private set; } = true;
			public bool ExportPaperLayouts { get; private set; }
			public bool FitModelToPaper { get; private set; } = true;
			public VerificationMode VerificationMode { get; private set; } = VerificationMode.ModelAudit;
			public double PaperWidthMm { get; private set; } = 420.0;
			public double PaperHeightMm { get; private set; } = 297.0;
			public double MarginMm { get; private set; } = 2.0;
			public List<string> FocusHandles { get; } = new List<string>();
			public double FocusPaddingModelUnits { get; private set; } = 25.0;
			public BoundingBox? ExplicitWindow { get; private set; }

			private bool _verificationModeExplicitlySet;

			public static Options Parse(IReadOnlyList<string> args)
			{
				var options = new Options();

				for (int i = 0; i < args.Count; i++)
				{
					string arg = args[i];
					switch (arg)
					{
						case "--input":
							options.InputPath = requireValue(args, ref i, arg);
							break;
						case "--output":
							options.OutputPdfPath = requireValue(args, ref i, arg);
							break;
						case "--report":
							options.ReportPath = requireValue(args, ref i, arg);
							break;
						case "--pipeline":
							string pipeline = requireValue(args, ref i, arg);
							options.UseSceneGraph = !string.Equals(pipeline, "legacy", StringComparison.OrdinalIgnoreCase);
							break;
						case "--verification-mode":
							options.VerificationMode = parseVerificationMode(requireValue(args, ref i, arg));
							options._verificationModeExplicitlySet = true;
							break;
						case "--mode":
							applyMode(options, requireValue(args, ref i, arg));
							break;
						case "--full-scale":
							options.FitModelToPaper = false;
							break;
						case "--paper-format":
							applyPaperFormat(options, requireValue(args, ref i, arg));
							break;
						case "--paper-width-mm":
							options.PaperWidthMm = double.Parse(requireValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
							break;
						case "--paper-height-mm":
							options.PaperHeightMm = double.Parse(requireValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
							break;
						case "--margin-mm":
							options.MarginMm = double.Parse(requireValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
							break;
						case "--focus-handle":
							options.FocusHandles.Add(requireValue(args, ref i, arg));
							break;
						case "--focus-padding-model":
							options.FocusPaddingModelUnits = double.Parse(requireValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
							break;
						case "--window":
							options.ExplicitWindow = parseWindow(args, ref i);
							break;
						case "--help":
						case "-h":
						case "/?":
							printUsageAndExit(0);
							break;
						default:
							throw new ArgumentException($"Unknown argument: {arg}");
					}
				}

				if (string.IsNullOrWhiteSpace(options.InputPath))
				{
					printUsageAndExit(1);
				}

				options.finalizeVerificationMode();
				options.InputPath = Path.GetFullPath(options.InputPath);
				options.OutputPdfPath ??= Path.ChangeExtension(options.InputPath, options.UseSceneGraph ? ".scenegraph.pdf" : ".legacy.pdf");
				options.OutputPdfPath = Path.GetFullPath(options.OutputPdfPath);
				if (!string.IsNullOrWhiteSpace(options.ReportPath))
				{
					options.ReportPath = Path.GetFullPath(options.ReportPath);
				}

				return options;
			}

			public string GetVerificationModeName()
			{
				return this.VerificationMode switch
				{
					VerificationMode.ModelAudit => "model-audit",
					VerificationMode.PublicationSheet => "publication-sheet",
					VerificationMode.FocusedWindow => "focused-window",
					_ => "model-audit",
				};
			}

			public bool TryGetWindow(out BoundingBox window)
			{
				if (this.ExplicitWindow.HasValue)
				{
					window = this.ExplicitWindow.Value;
					return true;
				}

				window = BoundingBox.Null;
				return false;
			}

			private static void applyMode(Options options, string mode)
			{
				switch ((mode ?? string.Empty).Trim().ToLowerInvariant())
				{
					case "model":
						options.ExportModelSpace = true;
						options.ExportPaperLayouts = false;
						break;
					case "layouts":
						options.ExportModelSpace = false;
						options.ExportPaperLayouts = true;
						break;
					case "both":
						options.ExportModelSpace = true;
						options.ExportPaperLayouts = true;
						break;
					default:
						throw new ArgumentException($"Unknown mode: {mode}");
				}
			}

			private static void applyPaperFormat(Options options, string formatValue)
			{
				string value = (formatValue ?? string.Empty).Trim();
				if (string.IsNullOrWhiteSpace(value))
				{
					throw new ArgumentException("Paper format must not be empty.");
				}

				bool landscape = true;
				string format = value;
				string normalized = value.Replace('_', '-');

				if (normalized.EndsWith("-portrait", StringComparison.OrdinalIgnoreCase) ||
					normalized.EndsWith(":portrait", StringComparison.OrdinalIgnoreCase))
				{
					landscape = false;
					format = stripPaperOrientationSuffix(normalized, "portrait");
				}
				else if (normalized.EndsWith("-landscape", StringComparison.OrdinalIgnoreCase) ||
					normalized.EndsWith(":landscape", StringComparison.OrdinalIgnoreCase))
				{
					landscape = true;
					format = stripPaperOrientationSuffix(normalized, "landscape");
				}

				if (!_paperFormats.TryGetValue(format, out var size))
				{
					throw new ArgumentException($"Unknown paper format: {formatValue}");
				}

				options.PaperWidthMm = landscape ? Math.Max(size.WidthMm, size.HeightMm) : Math.Min(size.WidthMm, size.HeightMm);
				options.PaperHeightMm = landscape ? Math.Min(size.WidthMm, size.HeightMm) : Math.Max(size.WidthMm, size.HeightMm);
			}

			private void finalizeVerificationMode()
			{
				if (!this._verificationModeExplicitlySet)
				{
					if (this.ExplicitWindow.HasValue || this.FocusHandles.Count > 0)
					{
						this.VerificationMode = VerificationMode.FocusedWindow;
					}
					else if (this.ExportPaperLayouts && !this.ExportModelSpace)
					{
						this.VerificationMode = VerificationMode.PublicationSheet;
					}
					else
					{
						this.VerificationMode = VerificationMode.ModelAudit;
					}

					return;
				}

				switch (this.VerificationMode)
				{
					case VerificationMode.ModelAudit:
						this.ExportModelSpace = true;
						this.ExportPaperLayouts = false;
						break;
					case VerificationMode.PublicationSheet:
						this.ExportModelSpace = false;
						this.ExportPaperLayouts = true;
						break;
					case VerificationMode.FocusedWindow:
						this.ExportModelSpace = true;
						this.ExportPaperLayouts = false;
						this.FitModelToPaper = true;
						if (!this.ExplicitWindow.HasValue && this.FocusHandles.Count == 0)
						{
							throw new ArgumentException("focused-window verification requires --window or --focus-handle.");
						}
						break;
				}
			}

			private static string requireValue(IReadOnlyList<string> args, ref int index, string arg)
			{
				if (index + 1 >= args.Count)
				{
					throw new ArgumentException($"Missing value for {arg}");
				}

				index += 1;
				return args[index];
			}

			private static BoundingBox parseWindow(IReadOnlyList<string> args, ref int index)
			{
				double minX = double.Parse(requireValue(args, ref index, "--window"), System.Globalization.CultureInfo.InvariantCulture);
				double minY = double.Parse(requireValue(args, ref index, "--window"), System.Globalization.CultureInfo.InvariantCulture);
				double maxX = double.Parse(requireValue(args, ref index, "--window"), System.Globalization.CultureInfo.InvariantCulture);
				double maxY = double.Parse(requireValue(args, ref index, "--window"), System.Globalization.CultureInfo.InvariantCulture);
				return new BoundingBox(
					new XYZ(Math.Min(minX, maxX), Math.Min(minY, maxY), 0.0),
					new XYZ(Math.Max(minX, maxX), Math.Max(minY, maxY), 0.0));
			}

			private static VerificationMode parseVerificationMode(string value)
			{
				switch ((value ?? string.Empty).Trim().ToLowerInvariant())
				{
					case "model-audit":
						return VerificationMode.ModelAudit;
					case "publication-sheet":
						return VerificationMode.PublicationSheet;
					case "focused-window":
						return VerificationMode.FocusedWindow;
					default:
						throw new ArgumentException($"Unknown verification mode: {value}");
				}
			}

			private static string stripPaperOrientationSuffix(string value, string suffix)
			{
				string trim = value;
				if (trim.EndsWith($":{suffix}", StringComparison.OrdinalIgnoreCase))
				{
					return trim[..^(suffix.Length + 1)];
				}

				if (trim.EndsWith($"-{suffix}", StringComparison.OrdinalIgnoreCase))
				{
					return trim[..^(suffix.Length + 1)];
				}

				return trim;
			}

			private static void printUsageAndExit(int code)
			{
				Console.WriteLine("Usage:");
				Console.WriteLine("  ACadSharp.Pdf.Examples --input <file.dwg|file.dxf> [--output <file.pdf>] [--report <file.json>] [--pipeline scenegraph|legacy] [--verification-mode model-audit|publication-sheet|focused-window] [--mode model|layouts|both] [--full-scale] [--paper-format A3|A3-portrait|A3-landscape] [--paper-width-mm <n>] [--paper-height-mm <n>] [--margin-mm <n>] [--focus-handle <HEX>] [--focus-padding-model <n>] [--window <minX> <minY> <maxX> <maxY>]");
				Environment.Exit(code);
			}
		}
	}
}
