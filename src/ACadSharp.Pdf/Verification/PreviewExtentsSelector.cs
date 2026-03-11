using ACadSharp.Entities;
using CSMath;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ACadSharp.Pdf.Verification
{
	public enum VerificationMode
	{
		ModelAudit,
		PublicationSheet,
		FocusedWindow,
	}

	public sealed class PreviewExtentsSelection
	{
		public BoundingBox Limits { get; }
		public string Strategy { get; }
		public int CandidateCount { get; }
		public int IncludedCount { get; }
		public IReadOnlyList<string> IncludedHandles { get; }
		public int FilteredByHandleCount { get; }
		public int ClusterCount { get; }
		public double ConnectionDistance { get; }
		public IReadOnlyList<PreviewExtentsExclusion> ExcludedEntities { get; }

		public PreviewExtentsSelection(
			BoundingBox limits,
			string strategy,
			int candidateCount,
			int includedCount,
			IReadOnlyList<string> includedHandles,
			int filteredByHandleCount,
			int clusterCount,
			double connectionDistance,
			IReadOnlyList<PreviewExtentsExclusion> excludedEntities)
		{
			this.Limits = limits;
			this.Strategy = strategy ?? "unknown";
			this.CandidateCount = candidateCount;
			this.IncludedCount = includedCount;
			this.IncludedHandles = includedHandles ?? Array.Empty<string>();
			this.FilteredByHandleCount = filteredByHandleCount;
			this.ClusterCount = clusterCount;
			this.ConnectionDistance = connectionDistance;
			this.ExcludedEntities = excludedEntities ?? Array.Empty<PreviewExtentsExclusion>();
		}
	}

	public sealed class PreviewExtentsExclusion
	{
		public string Handle { get; }
		public string EntityType { get; }
		public string Reason { get; }
		public double? GapDistance { get; }

		public PreviewExtentsExclusion(string handle, string entityType, string reason, double? gapDistance = null)
		{
			this.Handle = handle ?? string.Empty;
			this.EntityType = entityType ?? string.Empty;
			this.Reason = reason ?? "unspecified";
			this.GapDistance = gapDistance;
		}
	}

	public static class PreviewExtentsSelector
	{
		public static bool TrySelect(
			IEnumerable<Entity> entities,
			IReadOnlyCollection<string> focusHandles,
			double paddingModelUnits,
			out PreviewExtentsSelection selection)
		{
			selection = null;

			HashSet<string> normalizedHandles = new HashSet<string>(
				(focusHandles ?? Array.Empty<string>())
					.Where(h => !string.IsNullOrWhiteSpace(h))
					.Select(h => h.Trim().ToUpperInvariant()));

			List<Candidate> candidates = new List<Candidate>();
			List<PreviewExtentsExclusion> exclusions = new List<PreviewExtentsExclusion>();
			int filteredByHandleCount = 0;

			foreach (Entity entity in entities ?? Array.Empty<Entity>())
			{
				if (entity == null)
				{
					continue;
				}

				string handle = entity.Handle.ToString("X");
				string entityType = entity.GetType().Name;

				if (normalizedHandles.Count > 0 && !normalizedHandles.Contains(handle))
				{
					filteredByHandleCount += 1;
					continue;
				}

				if (!tryGetPreviewBounds(entity, out BoundingBox box))
				{
					exclusions.Add(new PreviewExtentsExclusion(handle, entityType, "bounding-box-error"));
					continue;
				}

				if (box.Extent != BoundingBoxExtent.Finite && box.Extent != BoundingBoxExtent.Point)
				{
					exclusions.Add(new PreviewExtentsExclusion(handle, entityType, "non-finite-bounds"));
					continue;
				}

				if (!isFinite(box.Min.X) || !isFinite(box.Min.Y) || !isFinite(box.Max.X) || !isFinite(box.Max.Y))
				{
					exclusions.Add(new PreviewExtentsExclusion(handle, entityType, "non-finite-coordinates"));
					continue;
				}

				candidates.Add(new Candidate(handle, entityType, entity.Layer?.Name, box));
			}

			if (candidates.Count == 0)
			{
				return false;
			}

			List<Candidate> included = candidates;
			string strategy = normalizedHandles.Count > 0 ? "focused-handles" : "finite-entity-union";
			int clusterCount = 1;
			double connectionDistance = 0.0;

			if (normalizedHandles.Count == 0 && candidates.Count > 1)
			{
				connectionDistance = computeConnectionDistance(candidates);
				List<List<Candidate>> clusters = buildClusters(candidates, connectionDistance);
				clusterCount = clusters.Count;

				if (clusters.Count > 1)
				{
					List<Candidate> selectedCluster = clusters
						.OrderByDescending(c => c.Count)
						.ThenByDescending(clusterArea)
						.First();

					List<List<Candidate>> selectedClusters = clusterSemanticHitCount(selectedCluster) > 0
						? collectNearbyAnnotationClusters(clusters, selectedCluster)
						: new List<List<Candidate>> { selectedCluster };
					included = selectedClusters.SelectMany(c => c).ToList();
					strategy = selectedClusters.Count > 1
						? "clustered-main-component-with-annotations"
						: "clustered-main-component";

					foreach (List<Candidate> cluster in clusters)
					{
						if (selectedClusters.Any(selected => ReferenceEquals(selected, cluster)))
						{
							continue;
						}

						double gapDistance = computeClusterGap(cluster, included);
						foreach (Candidate candidate in cluster)
						{
							exclusions.Add(new PreviewExtentsExclusion(candidate.Handle, candidate.EntityType, "outlier-cluster", gapDistance));
						}
					}
				}

				List<Candidate> pruned = pruneOversizedContainers(included, exclusions);
				if (pruned.Count > 0)
				{
					included = pruned;
				}

				if (included.Count > 1)
				{
					connectionDistance = computeConnectionDistance(included);
					List<List<Candidate>> refinedClusters = buildClusters(included, connectionDistance);
					clusterCount = refinedClusters.Count;

					if (refinedClusters.Count > 1)
					{
						List<List<Candidate>> selectedClusters = selectPreferredClusters(refinedClusters, out string refinedStrategy);
						included = selectedClusters.SelectMany(c => c).ToList();
						strategy = refinedStrategy;

						foreach (List<Candidate> cluster in refinedClusters)
						{
							if (selectedClusters.Any(selected => ReferenceEquals(selected, cluster)))
							{
								continue;
							}

							double gapDistance = computeClusterGap(cluster, included);
							foreach (Candidate candidate in cluster)
							{
								exclusions.Add(new PreviewExtentsExclusion(candidate.Handle, candidate.EntityType, "outlier-cluster", gapDistance));
							}
						}
					}
				}
			}

			if (normalizedHandles.Count == 0
				&& strategy.StartsWith("clustered-semantic-component", StringComparison.Ordinal))
			{
				int beforeBridge = included.Count;
				included = mergeNearbyDimensionClusters(candidates, included);
				if (included.Count > beforeBridge)
				{
					strategy = "clustered-semantic-component-with-dimensions";
				}
			}

			HashSet<string> includedHandles = new HashSet<string>(included.Select(c => c.Handle), StringComparer.OrdinalIgnoreCase);
			exclusions = exclusions
				.Where(e => !includedHandles.Contains(e.Handle))
				.ToList();

			BoundingBox limits = BoundingBox.Null;
			foreach (Candidate candidate in included)
			{
				limits = limits.Merge(candidate.Box);
			}

			if (limits.Extent == BoundingBoxExtent.Null)
			{
				return false;
			}

			if (paddingModelUnits > 0.0)
			{
				limits = new BoundingBox(
					new XYZ(limits.Min.X - paddingModelUnits, limits.Min.Y - paddingModelUnits, limits.Min.Z),
					new XYZ(limits.Max.X + paddingModelUnits, limits.Max.Y + paddingModelUnits, limits.Max.Z));
			}

			selection = new PreviewExtentsSelection(
				limits,
				strategy,
				candidates.Count,
				included.Count,
				included.Select(c => c.Handle).ToArray(),
				filteredByHandleCount,
				clusterCount,
				connectionDistance,
				exclusions);
			return true;
		}

		private static List<List<Candidate>> buildClusters(IReadOnlyList<Candidate> candidates, double connectionDistance)
		{
			List<List<Candidate>> clusters = new List<List<Candidate>>();
			bool[] visited = new bool[candidates.Count];

			for (int i = 0; i < candidates.Count; i++)
			{
				if (visited[i])
				{
					continue;
				}

				List<Candidate> cluster = new List<Candidate>();
				Queue<int> queue = new Queue<int>();
				queue.Enqueue(i);
				visited[i] = true;

				while (queue.Count > 0)
				{
					int current = queue.Dequeue();
					cluster.Add(candidates[current]);

					for (int j = 0; j < candidates.Count; j++)
					{
						if (visited[j])
						{
							continue;
						}

						if (boxGap(candidates[current].Box, candidates[j].Box) <= connectionDistance)
						{
							visited[j] = true;
							queue.Enqueue(j);
						}
					}
				}

				clusters.Add(cluster);
			}

			return clusters;
		}

		private static double computeConnectionDistance(IReadOnlyList<Candidate> candidates)
		{
			if (candidates.Count <= 1)
			{
				return 1.0;
			}

			double[] nearestGaps = new double[candidates.Count];
			double[] diagonals = new double[candidates.Count];

			for (int i = 0; i < candidates.Count; i++)
			{
				double nearest = double.PositiveInfinity;
				for (int j = 0; j < candidates.Count; j++)
				{
					if (i == j)
					{
						continue;
					}

					double gap = boxGap(candidates[i].Box, candidates[j].Box);
					if (gap < nearest)
					{
						nearest = gap;
					}
				}

				nearestGaps[i] = double.IsInfinity(nearest) ? 0.0 : nearest;
				diagonals[i] = candidates[i].Diagonal;
			}

			double medianGap = median(nearestGaps);
			double medianDiagonal = median(diagonals);
			double madGap = median(nearestGaps.Select(g => Math.Abs(g - medianGap)));
			double robustGap = medianGap + (madGap * 6.0);

			return Math.Max(
				1.0,
				Math.Max(
					medianDiagonal * 20.0,
					Math.Max(medianGap * 8.0, robustGap * 2.0)));
		}

		private static double computeClusterGap(IReadOnlyList<Candidate> cluster, IReadOnlyList<Candidate> selectedCluster)
		{
			double minGap = double.PositiveInfinity;
			foreach (Candidate candidate in cluster)
			{
				foreach (Candidate selected in selectedCluster)
				{
					double gap = boxGap(candidate.Box, selected.Box);
					if (gap < minGap)
					{
						minGap = gap;
					}
				}
			}

			return double.IsInfinity(minGap) ? 0.0 : minGap;
		}

		private static double clusterArea(IReadOnlyList<Candidate> cluster)
		{
			BoundingBox union = unionOf(cluster);
			double width = Math.Max(0.0, union.Max.X - union.Min.X);
			double height = Math.Max(0.0, union.Max.Y - union.Min.Y);
			return width * height;
		}

		private static List<List<Candidate>> selectPreferredClusters(
			IReadOnlyList<List<Candidate>> clusters,
			out string strategy)
		{
			List<Candidate> semanticCluster = clusters
				.Where(c => c != null && c.Count > 0)
				.OrderByDescending(clusterSemanticHitCount)
				.ThenByDescending(c => c.Count)
				.ThenByDescending(clusterArea)
				.FirstOrDefault();

			if (semanticCluster != null && clusterSemanticHitCount(semanticCluster) > 0)
			{
				List<List<Candidate>> mergedClusters = collectNearbyAnnotationClusters(clusters, semanticCluster);
				strategy = mergedClusters.Count > 1
					? "clustered-semantic-component-with-annotations"
					: "clustered-semantic-component";
				return mergedClusters;
			}

			strategy = "clustered-main-component";
			return new List<List<Candidate>>
			{
				clusters
					.OrderByDescending(c => c.Count)
					.ThenByDescending(clusterArea)
					.First()
			};
		}

		private static List<List<Candidate>> collectNearbyAnnotationClusters(
			IReadOnlyList<List<Candidate>> clusters,
			List<Candidate> anchorCluster)
		{
			var selected = new List<List<Candidate>> { anchorCluster };
			BoundingBox anchorBounds = unionOf(anchorCluster);
			double mergeDistance = Math.Max(25.0, Math.Min(220.0, diagonal(anchorBounds) * 0.40));

			foreach (List<Candidate> cluster in clusters
				.Where(c => c != null && c.Count > 0)
				.Where(c => !ReferenceEquals(c, anchorCluster))
				.OrderBy(c => computeClusterGap(c, anchorCluster)))
			{
				if (!isAnnotationSupportCluster(cluster))
				{
					continue;
				}

				double gapDistance = computeClusterGap(cluster, selected.SelectMany(c => c).ToList());
				if (gapDistance > mergeDistance)
				{
					continue;
				}

				selected.Add(cluster);
			}

			return selected;
		}

		private static bool isAnnotationSupportCluster(IReadOnlyList<Candidate> cluster)
		{
			if (cluster == null || cluster.Count == 0)
			{
				return false;
			}

			int annotationCount = cluster.Count(c => isAnnotationEntityType(c.EntityType));
			return annotationCount >= Math.Max(2, (int)Math.Ceiling(cluster.Count * 0.8));
		}

		private static List<Candidate> mergeNearbyDimensionClusters(
			IReadOnlyList<Candidate> allCandidates,
			IReadOnlyList<Candidate> included)
		{
			List<Candidate> merged = included?.ToList() ?? new List<Candidate>();
			if (merged.Count == 0)
			{
				return merged;
			}

			BoundingBox anchorBounds = unionOf(merged);
			double mergeDistance = Math.Max(25.0, Math.Min(220.0, diagonal(anchorBounds) * 0.40));
			HashSet<string> includedHandles = new HashSet<string>(merged.Select(c => c.Handle), StringComparer.OrdinalIgnoreCase);
			List<Candidate> dimensionCandidates = (allCandidates ?? Array.Empty<Candidate>())
				.Where(c => c != null && isDimensionEntityType(c.EntityType))
				.ToList();

			if (dimensionCandidates.Count == 0)
			{
				return merged;
			}

			double connectionDistance = computeConnectionDistance(dimensionCandidates);
			List<List<Candidate>> clusters = buildClusters(dimensionCandidates, connectionDistance);
			foreach (List<Candidate> cluster in clusters
				.Where(c => c != null && c.Count > 0)
				.OrderBy(c => computeClusterGap(c, merged)))
			{
				if (cluster.All(c => includedHandles.Contains(c.Handle)))
				{
					continue;
				}

				double gapDistance = computeClusterGap(cluster, merged);
				if (gapDistance > mergeDistance)
				{
					continue;
				}

				foreach (Candidate candidate in cluster)
				{
					if (includedHandles.Add(candidate.Handle))
					{
						merged.Add(candidate);
					}
				}
			}

			return merged;
		}

		private static int clusterSemanticHitCount(IReadOnlyList<Candidate> cluster)
		{
			return cluster?.Count(c => hasSemanticLayerHint(c.LayerName)) ?? 0;
		}

		private static List<Candidate> pruneOversizedContainers(
			IReadOnlyList<Candidate> cluster,
			ICollection<PreviewExtentsExclusion> exclusions)
		{
			List<Candidate> remaining = cluster?.ToList() ?? new List<Candidate>();
			if (remaining.Count < 8)
			{
				return remaining;
			}

			for (int pass = 0; pass < 4 && remaining.Count >= 8; pass++)
			{
				double medianArea = median(remaining.Select(c => c.Area));
				double medianDiagonal = median(remaining.Select(c => c.Diagonal));
				if (medianArea <= 0.0)
				{
					medianArea = remaining.Where(c => c.Area > 0.0).DefaultIfEmpty().Min(c => c?.Area ?? 0.0);
				}

				if (medianDiagonal <= 0.0)
				{
					medianDiagonal = remaining.Where(c => c.Diagonal > 0.0).DefaultIfEmpty().Min(c => c?.Diagonal ?? 0.0);
				}

				if (medianArea <= 0.0 || medianDiagonal <= 0.0)
				{
					break;
				}

				BoundingBox union = unionOf(remaining);
				double unionArea = area(union);
				double unionWidth = Math.Max(0.0, union.Max.X - union.Min.X);
				double unionHeight = Math.Max(0.0, union.Max.Y - union.Min.Y);

				Candidate oversized = remaining
					.Where(c => c.IsInsert)
					.Where(c => c.Area >= Math.Max(50000.0, medianArea * 100.0))
					.Where(c => c.Diagonal >= Math.Max(250.0, medianDiagonal * 10.0))
					.OrderByDescending(c => c.Area)
					.FirstOrDefault(c => shouldPruneOversizedContainer(c, remaining, unionArea, unionWidth, unionHeight));

				if (oversized == null)
				{
					break;
				}

				remaining.Remove(oversized);
				exclusions?.Add(new PreviewExtentsExclusion(oversized.Handle, oversized.EntityType, "oversized-container"));
			}

			return remaining;
		}

		private static bool shouldPruneOversizedContainer(
			Candidate candidate,
			IReadOnlyList<Candidate> remaining,
			double unionArea,
			double unionWidth,
			double unionHeight)
		{
			if (candidate == null || remaining == null || remaining.Count < 8)
			{
				return false;
			}

			int overlaps = remaining.Count(other =>
				!ReferenceEquals(other, candidate) &&
				boxGap(candidate.Box, other.Box) <= 1e-6);
			double candidateWidth = Math.Max(0.0, candidate.Box.Max.X - candidate.Box.Min.X);
			double candidateHeight = Math.Max(0.0, candidate.Box.Max.Y - candidate.Box.Min.Y);
			double shortSide = Math.Max(1e-6, Math.Min(candidateWidth, candidateHeight));
			double aspectRatio = Math.Max(candidateWidth, candidateHeight) / shortSide;
			bool contextContainer = overlaps >= Math.Max(6, remaining.Count / 20);
			bool elongatedOutlier = aspectRatio >= 3.0;
			if (!contextContainer && !elongatedOutlier)
			{
				return false;
			}

			BoundingBox without = unionExcluding(remaining, candidate);
			if (without.Extent == BoundingBoxExtent.Null)
			{
				return false;
			}

			double areaWithout = area(without);
			double widthWithout = Math.Max(0.0, without.Max.X - without.Min.X);
			double heightWithout = Math.Max(0.0, without.Max.Y - without.Min.Y);

			bool shrinksArea = unionArea > 0.0 && areaWithout <= unionArea * 0.75;
			bool shrinksWidth = unionWidth > 0.0 && widthWithout <= unionWidth * 0.8;
			bool shrinksHeight = unionHeight > 0.0 && heightWithout <= unionHeight * 0.8;
			return shrinksArea || (shrinksWidth && shrinksHeight);
		}

		private static BoundingBox unionOf(IEnumerable<Candidate> candidates)
		{
			BoundingBox union = BoundingBox.Null;
			foreach (Candidate candidate in candidates ?? Array.Empty<Candidate>())
			{
				union = union.Merge(candidate.Box);
			}

			return union;
		}

		private static BoundingBox unionExcluding(IEnumerable<Candidate> candidates, Candidate excluded)
		{
			BoundingBox union = BoundingBox.Null;
			foreach (Candidate candidate in candidates ?? Array.Empty<Candidate>())
			{
				if (ReferenceEquals(candidate, excluded))
				{
					continue;
				}

				union = union.Merge(candidate.Box);
			}

			return union;
		}

		private static double area(BoundingBox box)
		{
			if (box.Extent == BoundingBoxExtent.Null)
			{
				return 0.0;
			}

			double width = Math.Max(0.0, box.Max.X - box.Min.X);
			double height = Math.Max(0.0, box.Max.Y - box.Min.Y);
			return width * height;
		}

		private static double diagonal(BoundingBox box)
		{
			if (box.Extent == BoundingBoxExtent.Null)
			{
				return 0.0;
			}

			double width = Math.Max(0.0, box.Max.X - box.Min.X);
			double height = Math.Max(0.0, box.Max.Y - box.Min.Y);
			return Math.Sqrt((width * width) + (height * height));
		}

		private static double boxGap(BoundingBox a, BoundingBox b)
		{
			double dx = Math.Max(0.0, Math.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X));
			double dy = Math.Max(0.0, Math.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y));
			return Math.Sqrt((dx * dx) + (dy * dy));
		}

		private static double median(IEnumerable<double> values)
		{
			double[] ordered = (values ?? Array.Empty<double>()).OrderBy(v => v).ToArray();
			if (ordered.Length == 0)
			{
				return 0.0;
			}

			int middle = ordered.Length / 2;
			if ((ordered.Length % 2) == 0)
			{
				return (ordered[middle - 1] + ordered[middle]) / 2.0;
			}

			return ordered[middle];
		}

		private static bool isFinite(double value)
		{
			return !double.IsNaN(value) && !double.IsInfinity(value);
		}

		private static bool tryGetPreviewBounds(Entity entity, out BoundingBox box)
		{
			box = BoundingBox.Null;
			if (entity == null)
			{
				return false;
			}

			try
			{
				box = entity.GetBoundingBox();
			}
			catch
			{
				return false;
			}

			if (entity is Dimension dimension
				&& dimension.Block?.Entities != null
				&& dimension.Block.Entities.Count > 0)
			{
				BoundingBox blockBounds = BoundingBox.Null;
				bool hasBlockBounds = false;
				foreach (Entity blockEntity in dimension.Block.Entities)
				{
					if (blockEntity == null)
					{
						continue;
					}

					try
					{
						BoundingBox blockBox = blockEntity.GetBoundingBox();
						if ((blockBox.Extent == BoundingBoxExtent.Finite || blockBox.Extent == BoundingBoxExtent.Point)
							&& isFinite(blockBox.Min.X)
							&& isFinite(blockBox.Min.Y)
							&& isFinite(blockBox.Max.X)
							&& isFinite(blockBox.Max.Y))
						{
							blockBounds = hasBlockBounds ? blockBounds.Merge(blockBox) : blockBox;
							hasBlockBounds = true;
						}
					}
					catch
					{
					}
				}

				if (hasBlockBounds)
				{
					box = box.Extent == BoundingBoxExtent.Null ? blockBounds : box.Merge(blockBounds);
				}
			}

			return true;
		}

		private static bool hasSemanticLayerHint(string layerName)
		{
			if (string.IsNullOrWhiteSpace(layerName))
			{
				return false;
			}

			string normalized = layerName.Trim().ToLowerInvariant();
			return normalized.Contains("карт") || normalized.Contains("cart");
		}

		private static bool isAnnotationEntityType(string entityType)
		{
			if (string.IsNullOrWhiteSpace(entityType))
			{
				return false;
			}

			return isDimensionEntityType(entityType)
				|| string.Equals(entityType, nameof(TextEntity), StringComparison.Ordinal)
				|| string.Equals(entityType, nameof(MText), StringComparison.Ordinal)
				|| string.Equals(entityType, nameof(Leader), StringComparison.Ordinal)
				|| string.Equals(entityType, nameof(MultiLeader), StringComparison.Ordinal)
				|| string.Equals(entityType, nameof(TableEntity), StringComparison.Ordinal);
		}

		private static bool isDimensionEntityType(string entityType)
		{
			return !string.IsNullOrWhiteSpace(entityType)
				&& entityType.IndexOf("Dimension", StringComparison.Ordinal) >= 0;
		}

		private sealed class Candidate
		{
			public string Handle { get; }
			public string EntityType { get; }
			public string LayerName { get; }
			public BoundingBox Box { get; }
			public bool IsInsert { get; }
			public double Area { get; }
			public double Diagonal { get; }

			public Candidate(string handle, string entityType, string layerName, BoundingBox box)
			{
				this.Handle = handle;
				this.EntityType = entityType;
				this.LayerName = layerName ?? string.Empty;
				this.Box = box;
				double width = Math.Max(0.0, box.Max.X - box.Min.X);
				double height = Math.Max(0.0, box.Max.Y - box.Min.Y);
				this.IsInsert = string.Equals(entityType, nameof(Insert), StringComparison.Ordinal);
				this.Area = width * height;
				this.Diagonal = Math.Sqrt((width * width) + (height * height));
			}
		}
	}
}
