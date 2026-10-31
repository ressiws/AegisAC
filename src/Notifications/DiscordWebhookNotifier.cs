using System.Net.Http;
using System.Text;
using System.Text.Json;
using AegisAC.Configuration;

namespace AegisAC.Notifications;

/// <summary>
/// Sends asynchronous Discord webhook notifications using Components V2.
/// </summary>
public static class DiscordWebhookNotifier
{
	private const int ComponentsV2Flag = 1 << 15;

	private static readonly HttpClient HttpClient = new()
	{
		Timeout = TimeSpan.FromSeconds(5)
	};

	/// <summary>
	/// Sends a formatted Components V2 alert asynchronously.
	/// </summary>
	public static void SendAlert(
		string webhookUrl,
		string serverId,
		string mapName,
		int serverTick,
		string playerName,
		ulong steamId,
		string moduleName,
		string detectionReason,
		PunishmentType punishment,
		string evidence = "")
	{
		if (string.IsNullOrWhiteSpace(webhookUrl))
			return;

		_ = Task.Run(async () =>
		{
			try
			{
				int accentColor = punishment switch
				{
					PunishmentType.Ban => 0xE5485D,
					PunishmentType.Kick => 0xE69A45,
					_ => 0x8794A8
				};

				string actionLabel = punishment switch
				{
					PunishmentType.Ban =>
						"🔴 **BANNED**",

					PunishmentType.Kick =>
						"🟠 **KICKED**",

					_ =>
						"⚪ **LOGGED ONLY** — No Punishment"
				};

				string riskLevel = moduleName switch
				{
					"DllInjection" or "CVarScanner" or "Anti-Untrusted" or "Exploits" =>
						"🔴 **CRITICAL RISK (Deterministic Violation)**",
					"SilentAim" or "Aimbot" or "AntiAim" or "Aimlock" =>
						"🟠 **HIGH RISK (Confirmed Multi-Incident)**",
					_ =>
						"🟡 **MEDIUM RISK (Heuristic Anomaly)**"
				};

				float confidencePct = moduleName switch
				{
					"DllInjection" or "CVarScanner" or "Anti-Untrusted" or "Exploits" => 99.9f,
					"SilentAim" or "Aimbot" or "AntiAim" or "Aimlock" => 98.5f,
					_ => 94.0f
				};

				string steamValue = steamId > 0
					? $"[`{steamId}`](https://steamcommunity.com/profiles/{steamId})"
					: "`N/A`";

				// Build the native Discord Components V2 layout.
				var components = new List<object>
				{
					Text(
						"### 🛡️ Aegis Anti-Cheat  `APP`\n" +
						"-# CS2 · Server-Sided Protection"
					),

					Separator(),

					Text(
						"## 🚨 CHEATER DETECTED\n" +
						$"Player **{EscapeMarkdown(playerName)}** " +
						"has been flagged by Aegis Anti-Cheat."
					),

					Separator(),

					Text(
						"### Server & Match Context\n" +
						$"**SERVER:** `{EscapeInline(serverId)}`\n" +
						$"**MAP:** `{EscapeInline(mapName)}`\n" +
						$"**TICK:** `{serverTick}`"
					),

					Separator(),

					Text(
						"### Player Information\n" +
						"**PLAYER**\n" +
						$"`{EscapeInline(playerName)}`\n\n" +
						"**STEAM ID**\n" +
						$"{steamValue}\n\n" +
						"**MODULE**\n" +
						$"`{EscapeInline(moduleName)}`"
					),

					Separator(),

					Text(
						"### Detection & Action\n" +
						"**DETECTION**\n" +
						$"`{EscapeInline(detectionReason)}`\n\n" +
						"**ACTION TAKEN**\n" +
						actionLabel + "\n\n" +
						"**RISK ASSESSMENT**\n" +
						$"{riskLevel} (`{confidencePct:F1}%`)"
					)
				};

				if (!string.IsNullOrWhiteSpace(evidence))
				{
					// Keep evidence within a reasonable display length.
					string trimmedEvidence = evidence.Length > 1000
						? evidence[..1000] + "..."
						: evidence;

					// Prevent evidence from breaking the Markdown code block.
					trimmedEvidence = trimmedEvidence.Replace(
						"```", "'''"
					);

					components.Add(Separator());

					components.Add(
						Text(
							"### 📊 Incident Evidence\n" +
							"```text\n" +
							trimmedEvidence +
							"\n```"
						)
					);
				}

				components.Add(Separator());

				components.Add(
					Text(
						"-# AEGIS ANTI-CHEAT FOR CS2 · " +
						$"SERVER-SIDED PROTECTION\n" +
						$"-# {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC"
					)
				);

				var container = new
				{
					type = 17, // Container
					accent_color = accentColor,
					components = components.ToArray()
				};

				string displayEvidence = string.IsNullOrWhiteSpace(evidence) ? "None" : (evidence.Length > 900 ? evidence[..900] + "..." : evidence);
				displayEvidence = displayEvidence.Replace("```", "'''");

				var embed = new
				{
					title = "🚨 CHEATER DETECTED — Aegis Anti-Cheat",
					description = $"Player **{EscapeMarkdown(playerName)}** has been flagged by Aegis Anti-Cheat.",
					color = accentColor,
					fields = new[]
					{
						new { name = "👤 Player", value = $"{EscapeInline(playerName)}\n{steamValue}", @inline = true },
						new { name = "🛡️ Detection", value = $"**Module:** `{EscapeInline(moduleName)}`\n**Action:** {actionLabel}", @inline = true },
						new { name = "🎮 Match Context", value = $"**Server:** `{EscapeInline(serverId)}`\n**Map:** `{EscapeInline(mapName)}`\n**Tick:** `{serverTick}`", @inline = true },
						new { name = "🎯 Risk & Confidence", value = $"{riskLevel} · Confidence: **{confidencePct:F1}%**", @inline = false },
						new { name = "📋 Detection Details", value = $"`{EscapeInline(detectionReason)}`", @inline = false },
						new { name = "📊 Incident Evidence", value = $"```text\n{displayEvidence}\n```", @inline = false }
					},
					footer = new { text = $"Aegis Anti-Cheat · Server-Sided Protection · {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC" },
					timestamp = DateTimeOffset.UtcNow.ToString("o")
				};

				var payload = new
				{
					username = "Aegis Anti-Cheat",
					avatar_url =
						"https://raw.githubusercontent.com/ressiws/AegisAC/main/.github/assets/logo.jpg",

					flags = ComponentsV2Flag,

					embeds = new object[]
					{
						embed
					},

					components = new object[]
					{
						container
					},

					allowed_mentions = new
					{
						parse = Array.Empty<string>()
					}
				};

				string json = JsonSerializer.Serialize(payload);

				using var content = new StringContent(
					json,
					Encoding.UTF8,
					"application/json"
				);

				// Components V2 webhooks require with_components=true.
				string url = AddComponentsQueryParameter(webhookUrl);

				using var response = await HttpClient.PostAsync(
					url,
					content
				).ConfigureAwait(false);

				if (!response.IsSuccessStatusCode && Utils.DebugMode)
				{
					string responseBody =
						await response.Content.ReadAsStringAsync()
							.ConfigureAwait(false);

					Utils.ConsoleLog(
						$"[DiscordWebhook] Failed: HTTP " +
						$"{(int)response.StatusCode} " +
						$"{response.ReasonPhrase}. {responseBody}"
					);
				}
			}
			catch (Exception ex)
			{
				if (Utils.DebugMode)
				{
					Utils.ConsoleLog(
						$"[DiscordWebhook] Error sending notification: " +
						ex.Message
					);
				}
			}
		});
	}

	private static object Text(string content)
	{
		return new
		{
			type = 10, // Text Display
			content
		};
	}

	private static object Separator()
	{
		return new
		{
			type = 14, // Separator
			divider = true,
			spacing = 1
		};
	}

	private static string AddComponentsQueryParameter(string url)
	{
		var builder = new UriBuilder(url);
		var query = builder.Query.TrimStart('?');

		// Avoid adding the parameter twice.
		bool alreadyPresent = query
			.Split('&', StringSplitOptions.RemoveEmptyEntries)
			.Any(parameter =>
				parameter.Split('=', 2)[0]
					.Equals(
						"with_components",
						StringComparison.OrdinalIgnoreCase
					)
			);

		if (!alreadyPresent)
		{
			builder.Query = string.IsNullOrEmpty(query)
				? "with_components=true"
				: $"{query}&with_components=true";
		}

		return builder.Uri.ToString();
	}

	private static string EscapeInline(string value)
	{
		return value
			.Replace("\\", "\\\\")
			.Replace("`", "\\`")
			.Replace("\r", " ")
			.Replace("\n", " ");
	}

	private static string EscapeMarkdown(string value)
	{
		return value
			.Replace("\\", "\\\\")
			.Replace("*", "\\*")
			.Replace("_", "\\_")
			.Replace("~", "\\~")
			.Replace("`", "\\`")
			.Replace("@", "@\u200B");
	}
}
