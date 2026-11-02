using System;
using System.Runtime.InteropServices;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Memory;

namespace AegisAC.Utilities.AntiWallHack;

/// <summary>
/// Hooks Source 2's CheckTransmit on CSource2GameEntities to withhold network packets
/// for occluded enemies, preventing client-side ESP and wallhacks from receiving their data.
/// </summary>
internal sealed unsafe class TransmitHook : IDisposable
{
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void CheckTransmitDelegate(
		nint self,
		nint infos,
		int count,
		nint unionBits,
		nint unknownBits,
		nint networkables,
		nint indices,
		int entityCount);

	private static int _playerSlotOffset = 0x240;
	private static int _fullUpdateOffset = 0x244;
	public const int MaxEntityCount = 16384;

	private readonly IUnmanagedFunction<CheckTransmitDelegate>? _function;
	private readonly Guid _hookGuid;
	private bool _disposed;

	public bool IsHooked => _function != null;

	public TransmitHook(ISwiftlyCore core, Action<nint, int> onPostTransmit, Action<Exception> onFailure)
	{
		try
		{
			try
			{
				int slotOff = core.GameData.GetOffset("CTransmitInfo::PlayerSlot");
				if (slotOff > 0)
				{
					_playerSlotOffset = slotOff;
				}
			}
			catch
			{
				// Keep default 0x240 (576) from verified CS2 binary layout
			}

			try
			{
				int fullOff = core.GameData.GetOffset("CTransmitInfo::FullUpdate");
				if (fullOff > 0)
				{
					_fullUpdateOffset = fullOff;
				}
			}
			catch
			{
				// Keep default 0x244 (580) from verified CS2 binary layout
			}

			nint table = core.Memory.GetVTableAddress("server", "CSource2GameEntities") ?? 0;
			if (table == 0)
			{
				throw new InvalidOperationException("CSource2GameEntities vtable address unavailable.");
			}

			int offset = core.GameData.GetOffset("ISource2GameEntities::CheckTransmit");
			if (offset <= 0)
			{
				throw new InvalidOperationException("ISource2GameEntities::CheckTransmit offset unavailable in GameData.");
			}

			_function = core.Memory.GetUnmanagedFunctionByVTable<CheckTransmitDelegate>(table, offset);
			_hookGuid = _function.AddHook(next => (self, infos, count, unionBits, unknownBits, networkables, indices, entityCount) =>
			{
				next()(self, infos, count, unionBits, unknownBits, networkables, indices, entityCount);
				try
				{
					onPostTransmit(infos, count);
				}
				catch (Exception ex)
				{
					onFailure(ex);
				}
			});
		}
		catch (Exception ex)
		{
			_function = null;
			onFailure(ex);
		}
	}

	public static bool TryReadInfo(nint infoPtr, out int slot, out nint primary, out nint suppressed)
	{
		slot = -1;
		primary = 0;
		suppressed = 0;

		if (infoPtr == 0)
		{
			return false;
		}

		slot = *(int*)((byte*)infoPtr + _playerSlotOffset);
		if (slot is < 0 or >= 64)
		{
			return false;
		}

		primary = *(nint*)infoPtr;
		suppressed = *(nint*)((byte*)infoPtr + 8);
		return primary != 0;
	}

	public static bool WithholdEntity(uint* primaryMask, uint* dontTransmitMask, int entityIndex)
	{
		if (primaryMask == null || entityIndex <= 0 || entityIndex >= MaxEntityCount)
		{
			return false;
		}

		int wordIndex = entityIndex >> 5;
		uint bit = 1u << (entityIndex & 31);

		if ((primaryMask[wordIndex] & bit) == 0)
		{
			return false;
		}

		// Register in suppression mask if available and distinct
		if (dontTransmitMask != null && dontTransmitMask != primaryMask)
		{
			dontTransmitMask[wordIndex] |= bit;
		}

		// Clear transmission bit so the entity packet is withheld from the client
		primaryMask[wordIndex] &= ~bit;
		return true;
	}

	public void Dispose()
	{
		if (!_disposed && _function != null)
		{
			_disposed = true;
			try
			{
				_function.RemoveHook(_hookGuid);
			}
			catch { }
		}
	}
}
