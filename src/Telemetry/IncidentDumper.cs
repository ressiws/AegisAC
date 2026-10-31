using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace AegisAC.Telemetry;

/// <summary>
/// Writes structured JSON telemetry incident dumps to disk asynchronously.
/// Allows server admins and staff to inspect exact tick, target bearing, and evidence for demo reviews.
/// </summary>
public static class IncidentDumper
{
	public static void DumpIncident(
		string directory,
		string serverId,
		string mapName,
		int serverTick,
		string playerName,
		ulong steamId,
		int userId,
		int slot,
		string moduleName,
		string detectionReason,
		string punishment,
		string evidence)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				string targetDir = string.IsNullOrWhiteSpace(directory) ? "logs/aegisac/incidents" : directory;
				if (!Directory.Exists(targetDir))
				{
					Directory.CreateDirectory(targetDir);
				}

				string fileName = $"{steamId}_{serverTick}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
				string fullPath = Path.Combine(targetDir, fileName);

				var payload = new
				{
					timestamp = DateTime.UtcNow.ToString("o"),
					server_id = serverId,
					map = mapName,
					server_tick = serverTick,
					player = new
					{
						name = playerName,
						steam_id = steamId.ToString(),
						user_id = userId,
						slot = slot
					},
					detection = new
					{
						module = moduleName,
						reason = detectionReason,
						punishment = punishment,
						evidence = evidence
					}
				};

				string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
				await File.WriteAllTextAsync(fullPath, json).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				if (Utils.DebugMode)
				{
					Utils.ConsoleLog($"[IncidentDumper] Failed to dump incident JSON: {ex.Message}");
				}
			}
		});
	}
}
