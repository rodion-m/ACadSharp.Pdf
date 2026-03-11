using ACadSharp.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ACadSharp.Pdf.Examples
{
	internal sealed class NotificationCollector
	{
		private static readonly Regex s_digits = new Regex(@"\d+", RegexOptions.Compiled);
		private readonly Dictionary<(NotificationType Type, string Key), NotificationBucket> _buckets = new();

		public void OnNotification(object sender, NotificationEventArgs e)
		{
			if (e == null)
			{
				return;
			}

			string key = normalize(e.Message);
			var bucketKey = (e.NotificationType, key);
			if (!this._buckets.TryGetValue(bucketKey, out NotificationBucket bucket))
			{
				bucket = new NotificationBucket(e.NotificationType, key, e.Message);
				this._buckets.Add(bucketKey, bucket);
			}

			bucket.Count += 1;
		}

		public IReadOnlyList<object> CreateSummary(int top = 50)
		{
			return this._buckets.Values
				.OrderByDescending(b => severityRank(b.Type))
				.ThenByDescending(b => b.Count)
				.ThenBy(b => b.Sample, StringComparer.Ordinal)
				.Take(top)
				.Select(b => (object)new
				{
					type = b.Type.ToString(),
					count = b.Count,
					message = b.Sample,
					key = b.Key,
				})
				.ToArray();
		}

		public void FlushToConsole(int perTypeLimit = 12)
		{
			foreach (IGrouping<NotificationType, NotificationBucket> group in this._buckets.Values
				.OrderByDescending(b => severityRank(b.Type))
				.ThenByDescending(b => b.Count)
				.GroupBy(b => b.Type))
			{
				int emitted = 0;
				foreach (NotificationBucket bucket in group.OrderByDescending(b => b.Count).ThenBy(b => b.Sample, StringComparer.Ordinal))
				{
					if (emitted >= perTypeLimit)
					{
						int remaining = group.Skip(emitted).Sum(b => b.Count);
						if (remaining > 0)
						{
							write(group.Key, $"... suppressed {remaining} additional notifications of type {group.Key}.");
						}
						break;
					}

					string suffix = bucket.Count > 1 ? $" (x{bucket.Count})" : string.Empty;
					write(group.Key, $"{bucket.Sample}{suffix}");
					emitted += 1;
				}
			}
		}

		private static int severityRank(NotificationType type)
		{
			return type switch
			{
				NotificationType.Error => 4,
				NotificationType.NotSupported => 3,
				NotificationType.Warning => 2,
				NotificationType.NotImplemented => 1,
				_ => 0,
			};
		}

		private static string normalize(string message)
		{
			if (string.IsNullOrWhiteSpace(message))
			{
				return string.Empty;
			}

			string normalized = s_digits.Replace(message, "#");
			return normalized.Trim();
		}

		private static void write(NotificationType type, string message)
		{
			switch (type)
			{
				case NotificationType.NotImplemented:
					Console.ForegroundColor = ConsoleColor.Gray;
					break;
				case NotificationType.Warning:
					Console.ForegroundColor = ConsoleColor.Yellow;
					break;
				case NotificationType.Error:
				case NotificationType.NotSupported:
					Console.ForegroundColor = ConsoleColor.Red;
					break;
				default:
					Console.ForegroundColor = ConsoleColor.White;
					break;
			}

			Console.WriteLine(message);
			Console.ResetColor();
		}

		private sealed class NotificationBucket
		{
			public NotificationType Type { get; }
			public string Key { get; }
			public string Sample { get; }
			public int Count { get; set; }

			public NotificationBucket(NotificationType type, string key, string sample)
			{
				this.Type = type;
				this.Key = key;
				this.Sample = sample ?? string.Empty;
			}
		}
	}
}
