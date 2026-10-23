using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// The two maps of a terrain a road layer covers.
/// </summary>
internal enum RoadLayerMap
{
	/// <summary>
	/// The height samples, shaped by Conform Terrain To Roads.
	/// </summary>
	Heights,

	/// <summary>
	/// The control map texels, painted by Paint Terrain Along Roads.
	/// </summary>
	Paint
}



/// <summary>
/// Laying the roads into a terrain through its <see cref="RoadTerrainLayer"/>, and taking them back out.
///
/// Under every sample the roads hold, the layer keeps the ground and what the roads wrote. Each time the roads are
/// laid in, every sample they held first gets its ground back, and only then is written again, from that ground:
/// so a conform can be pressed any number of times without the result creeping outward (each one used to start
/// from what the last one left), a sample a road has moved away from goes back to the ground, and the whole layer
/// can be taken out again. A sample changed by hand since the roads wrote it keeps the change: a height sculpted
/// up or down moves its ground as much, a texel painted over keeps its new paint.
/// </summary>
internal static class RoadTerrainLayers
{
	/// <summary>
	/// Leads every packed layer, so data in a format changed later is told apart instead of misread.
	/// </summary>
	private const byte PackVersion = 1;



	/// <summary>
	/// The terrains in a scene the roads can be laid into.
	/// </summary>
	public static List<Terrain> TerrainsIn(Scene _Scene)
	{
		if (_Scene is null)
			return [];

		return _Scene.GetAllComponents<Terrain>().Where(terrain => terrain.IsValid() && terrain.Storage is not null).ToList();
	}



	/// <summary>
	/// The road layer the roads are laid into a terrain through, or null, with why, when it has none to use.
	/// </summary>
	public static RoadTerrainLayer LayerOf(Terrain _Terrain, out string _Problem)
	{
		var layer = _Terrain.GetComponent<RoadTerrainLayer>(true);

		if (layer is null || !layer.IsStarted)
		{
			_Problem = $"\"{_Terrain.GameObject.Name}\" has no road layer yet: press Start Road Layer first, while no road is shaped into the terrain.";
			return null;
		}

		if (layer.Resolution != _Terrain.Storage.Resolution)
		{
			_Problem = $"\"{_Terrain.GameObject.Name}\" was resampled since its road layer was started, so the layer no longer lines up with it: start the road layer again.";
			return null;
		}

		_Problem = null;

		return layer;
	}



	/// <summary>
	/// Where each terrain's road layer stands, a line per terrain, for the sidebar.
	/// </summary>
	public static string Describe(IReadOnlyList<Terrain> _Terrains)
	{
		if (_Terrains.Count == 0)
			return "No terrain in the scene.";

		var lines = new List<string>(_Terrains.Count);

		foreach (var terrain in _Terrains)
		{
			var layer = terrain.GetComponent<RoadTerrainLayer>(true);

			string state = layer is null || !layer.IsStarted ? "Not started."
				: layer.Resolution != terrain.Storage.Resolution ? "Out of line with the terrain, which was resampled since: start it again."
				: layer.HeightSamples + layer.PaintTexels == 0 ? "Started, holding nothing yet."
				: $"Holding {layer.HeightSamples:N0} shaped height samples and {layer.PaintTexels:N0} painted texels.";

			lines.Add(_Terrains.Count == 1 ? state : $"{terrain.GameObject.Name}: {state}");
		}

		return string.Join("\n", lines);
	}



	/// <summary>
	/// How many samples the road layers on these terrains hold, shaped and painted together; a layer its terrain was
	/// resampled away from holds nothing that can still be used.
	/// </summary>
	public static int HeldBy(IReadOnlyList<Terrain> _Terrains)
	{
		int held = 0;

		foreach (var terrain in _Terrains)
		{
			if (terrain.GetComponent<RoadTerrainLayer>(true) is { IsStarted: true } layer && layer.Resolution == terrain.Storage.Resolution)
				held += layer.HeightSamples + layer.PaintTexels;
		}

		return held;
	}



	/// <summary>
	/// Starts the road layer on each terrain: the terrain as it is now becomes the ground the roads are laid over.
	/// A layer started before forgets what it held, and whatever the roads did under it stays in the terrain as
	/// part of the new ground. One undo step.
	/// </summary>
	public static void Start(IReadOnlyList<Terrain> _Terrains)
	{
		var existing = _Terrains.Select(terrain => terrain.GetComponent<RoadTerrainLayer>(true)).Where(layer => layer.IsValid()).ToList();

		using (SceneEditorSession.Active.UndoScope("Start Road Layer").WithComponentCreations().WithComponentChanges(existing).Push())
		{
			foreach (var terrain in _Terrains)
			{
				var layer = terrain.GetComponent<RoadTerrainLayer>(true) ?? terrain.AddComponent<RoadTerrainLayer>();

				layer.Resolution = terrain.Storage.Resolution;
				layer.Heights = "";
				layer.Paint = "";
				layer.HeightSamples = 0;
				layer.PaintTexels = 0;
			}
		}
	}



	/// <summary>
	/// Takes the roads back out of each terrain: every sample a road layer holds gets its ground back. The layers
	/// stay started, holding nothing, so the roads can be laid in again from that ground. Returns how many samples
	/// changed.
	/// </summary>
	public static int Remove(IReadOnlyList<Terrain> _Terrains, RoadTerrainUndo _Undo, out string _Problem)
	{
		_Problem = null;

		int changed = 0;
		var none = new HashSet<int>();

		foreach (var terrain in _Terrains)
		{
			// A terrain whose layer was never started has nothing to give back.
			if (terrain.GetComponent<RoadTerrainLayer>(true) is not { IsStarted: true })
				continue;

			var layer = LayerOf(terrain, out string reason);

			if (layer is not null)
			{
				changed += Lay(terrain, layer, RoadLayerMap.Heights, none, null, _Undo, out _, out string heights);
				changed += Lay(terrain, layer, RoadLayerMap.Paint, none, null, _Undo, out _, out string paint);
				reason = heights ?? paint;
			}

			_Problem ??= reason;
		}

		return changed;
	}



	/// <summary>
	/// Lays the roads into one map of a terrain through its road layer, and returns how many of the map's samples
	/// changed. Every sample the layer held gets its ground back; every sample in <paramref name="_Reached"/> is
	/// then set to what <paramref name="_Write"/> makes of its ground; and the layer is left holding exactly the
	/// samples that are off their ground. With nothing reached, the roads are taken out. The change is added to
	/// <paramref name="_Undo"/>; <paramref name="_Held"/> is how many samples the layer holds after it.
	/// </summary>
	public static int Lay(Terrain _Terrain, RoadTerrainLayer _Layer, RoadLayerMap _Map, HashSet<int> _Reached, Func<int, uint, uint> _Write, RoadTerrainUndo _Undo, out int _Held, out string _Problem)
	{
		var storage = _Terrain.Storage;
		int resolution = storage.Resolution;
		bool heights = _Map == RoadLayerMap.Heights;
		string packed = heights ? _Layer.Heights : _Layer.Paint;
		int heldBefore = heights ? _Layer.HeightSamples : _Layer.PaintTexels;

		_Held = heldBefore;

		if (!TryUnpack(packed, resolution * resolution, out var held))
		{
			_Problem = $"The road layer on \"{_Terrain.GameObject.Name}\" can't be read back, so it was left alone: start the road layer again.";
			return 0;
		}

		_Problem = null;

		var next = new Dictionary<int, (uint Ground, uint Written)>(_Reached.Count);
		var indices = new List<int>();
		var before = new List<uint>();
		var after = new List<uint>();

		foreach (int index in _Reached)
			Visit(index, true);

		foreach (int index in held.Keys)
		{
			if (!_Reached.Contains(index))
				Visit(index, false);
		}

		_Held = next.Count;

		string packedNext = Pack(next);

		// Pressed again with nothing changed since: the terrain already is what the roads make of the ground.
		if (indices.Count == 0 && packedNext == packed)
			return 0;

		int[] changed = indices.ToArray();
		uint[] was = before.ToArray();
		uint[] now = after.ToArray();
		var region = Bounds(changed, resolution);
		int heldAfter = next.Count;

		Apply(_Terrain, _Map, resolution, changed, now, region, packedNext, heldAfter);

		_Undo.Add(() => Apply(_Terrain, _Map, resolution, changed, was, region, packed, heldBefore),
			() => Apply(_Terrain, _Map, resolution, changed, now, region, packedNext, heldAfter));

		return changed.Length;

		void Visit(int _Index, bool _Wanted)
		{
			uint current = heights ? storage.HeightMap[_Index] : storage.ControlMap[_Index];
			uint ground = held.TryGetValue(_Index, out var sample) ? GroundUnder(heights, current, sample) : current;
			uint written = _Wanted ? _Write(_Index, ground) : ground;

			// Only what the roads move off the ground is theirs to remember.
			if (written != ground)
				next[_Index] = (ground, written);

			if (written == current)
				return;

			indices.Add(_Index);
			before.Add(current);
			after.Add(written);
		}
	}



	/// <summary>
	/// The ground under a sample the layer holds. While the sample still carries what the roads wrote (or has been
	/// put back on its ground) it is the ground the layer remembers. Changed by hand since, the change is kept: a
	/// height sculpted up or down moves the ground as much, a texel painted over keeps its new paint.
	/// </summary>
	private static uint GroundUnder(bool _Heights, uint _Current, (uint Ground, uint Written) _Sample)
	{
		if (_Current == _Sample.Written || _Current == _Sample.Ground)
			return _Sample.Ground;

		if (!_Heights)
			return _Current;

		return (uint)Math.Clamp((long)_Sample.Ground + _Current - _Sample.Written, 0L, ushort.MaxValue);
	}



	/// <summary>
	/// Puts samples into one of a terrain's maps and the packed samples into its road layer, then brings the
	/// terrain's rendering and collision up to date.
	/// </summary>
	private static void Apply(Terrain _Terrain, RoadLayerMap _Map, int _Resolution, int[] _Indices, uint[] _Values, RectInt _Region, string _Packed, int _Held)
	{
		// A terrain deleted or resampled since has nothing these samples line up with.
		if (!_Terrain.IsValid() || _Terrain.Storage is null || _Terrain.Storage.Resolution != _Resolution)
			return;

		var storage = _Terrain.Storage;
		bool heights = _Map == RoadLayerMap.Heights;

		for (int i = 0; i < _Indices.Length; i++)
		{
			if (heights)
				storage.HeightMap[_Indices[i]] = (ushort)_Values[i];
			else
				storage.ControlMap[_Indices[i]] = _Values[i];
		}

		if (_Terrain.GetComponent<RoadTerrainLayer>(true) is { } layer)
		{
			if (heights)
			{
				layer.Heights = _Packed;
				layer.HeightSamples = _Held;
			}
			else
			{
				layer.Paint = _Packed;
				layer.PaintTexels = _Held;
			}
		}

		if (_Indices.Length > 0)
			_Terrain.ApplyStorageChanges(heights ? Terrain.SyncFlags.Height : Terrain.SyncFlags.Control, _Region);
	}



	/// <summary>
	/// The rectangle of the grid round a set of samples.
	/// </summary>
	private static RectInt Bounds(int[] _Indices, int _Resolution)
	{
		if (_Indices.Length == 0)
			return default;

		int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

		foreach (int index in _Indices)
		{
			int x = index % _Resolution;
			int y = index / _Resolution;

			left = Math.Min(left, x);
			right = Math.Max(right, x);
			top = Math.Min(top, y);
			bottom = Math.Max(bottom, y);
		}

		return new RectInt(left, top, right - left + 1, bottom - top + 1);
	}



	/// <summary>
	/// Packs a layer's samples for the scene. In index order, each is written as the gap since the one before, its
	/// ground as the step from that one's ground, and how far the roads moved it off its ground as the step from how
	/// far they moved that one, all as variable-length numbers. Along a road those are mostly zero or close to it,
	/// which is what lets the deflate after squeeze them down.
	/// </summary>
	private static string Pack(Dictionary<int, (uint Ground, uint Written)> _Samples)
	{
		if (_Samples.Count == 0)
			return "";

		int[] indices = _Samples.Keys.ToArray();
		Array.Sort(indices);

		using var payload = new MemoryStream(indices.Length * 4 + 8);

		WriteNumber(payload, (ulong)indices.Length);

		int previousIndex = -1;
		long previousGround = 0;
		long previousOffset = 0;

		foreach (int index in indices)
		{
			var (ground, written) = _Samples[index];
			long offset = (long)written - ground;

			WriteNumber(payload, (ulong)(index - previousIndex - 1));
			WriteNumber(payload, ZigZag(ground - previousGround));
			WriteNumber(payload, ZigZag(offset - previousOffset));

			previousIndex = index;
			previousGround = ground;
			previousOffset = offset;
		}

		using var packed = new MemoryStream();

		packed.WriteByte(PackVersion);

		using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, true))
			payload.WriteTo(deflate);

		return Convert.ToBase64String(packed.GetBuffer(), 0, (int)packed.Length);
	}



	/// <summary>
	/// Reads packed samples back; false when they can't be, written in a later format or damaged.
	/// </summary>
	private static bool TryUnpack(string _Packed, int _SampleCount, out Dictionary<int, (uint Ground, uint Written)> _Samples)
	{
		_Samples = new Dictionary<int, (uint Ground, uint Written)>();

		if (string.IsNullOrEmpty(_Packed))
			return true;

		byte[] payload;

		try
		{
			byte[] packed = Convert.FromBase64String(_Packed);

			if (packed.Length == 0 || packed[0] != PackVersion)
				return false;

			using var deflate = new DeflateStream(new MemoryStream(packed, 1, packed.Length - 1), CompressionMode.Decompress);
			using var inflated = new MemoryStream();

			deflate.CopyTo(inflated);
			payload = inflated.ToArray();
		}
		catch (Exception exception) when (exception is FormatException or InvalidDataException)
		{
			return false;
		}

		int position = 0;

		if (!TryReadNumber(payload, ref position, out ulong count))
			return false;

		int previousIndex = -1;
		long previousGround = 0;
		long previousOffset = 0;

		for (ulong i = 0; i < count; i++)
		{
			if (!TryReadNumber(payload, ref position, out ulong gap) || !TryReadNumber(payload, ref position, out ulong groundStep) || !TryReadNumber(payload, ref position, out ulong offsetStep))
				return false;

			long index = previousIndex + 1L + (long)gap;
			long ground = previousGround + UnZigZag(groundStep);
			long offset = previousOffset + UnZigZag(offsetStep);
			long written = ground + offset;

			if (index < 0 || index >= _SampleCount || ground < 0 || ground > uint.MaxValue || written < 0 || written > uint.MaxValue)
				return false;

			_Samples[(int)index] = ((uint)ground, (uint)written);

			previousIndex = (int)index;
			previousGround = ground;
			previousOffset = offset;
		}

		return true;
	}



	private static void WriteNumber(Stream _Stream, ulong _Value)
	{
		while (_Value >= 0x80)
		{
			_Stream.WriteByte((byte)(_Value | 0x80));
			_Value >>= 7;
		}

		_Stream.WriteByte((byte)_Value);
	}



	private static bool TryReadNumber(byte[] _Bytes, ref int _Position, out ulong _Value)
	{
		_Value = 0;

		for (int shift = 0; shift < 64; shift += 7)
		{
			if (_Position >= _Bytes.Length)
				return false;

			byte part = _Bytes[_Position++];

			_Value |= (ulong)(part & 0x7F) << shift;

			if ((part & 0x80) == 0)
				return true;
		}

		return false;
	}



	/// <summary>
	/// Folds a signed step into an unsigned one, small either way round: 0, -1, 1, -2, 2... become 0, 1, 2, 3, 4...
	/// </summary>
	private static ulong ZigZag(long _Value)
	{
		return (ulong)((_Value << 1) ^ (_Value >> 63));
	}



	private static long UnZigZag(ulong _Value)
	{
		return (long)(_Value >> 1) ^ -(long)(_Value & 1);
	}
}



/// <summary>
/// What one press of a Terrain tool button changed, over every terrain in the scene: taken back and redone as a
/// single step.
/// </summary>
internal sealed class RoadTerrainUndo
{
	private readonly List<Action> m_Undo = [];
	private readonly List<Action> m_Redo = [];



	public void Add(Action _Undo, Action _Redo)
	{
		m_Undo.Add(_Undo);
		m_Redo.Add(_Redo);
	}



	/// <summary>
	/// Records the step, if anything changed. The scene is marked as changed with it: the road layers live in the
	/// scene, and must be saved along with the terrains or they no longer match them.
	/// </summary>
	public void Push(string _Title)
	{
		if (m_Undo.Count == 0)
			return;

		Action[] undo = m_Undo.ToArray();
		Action[] redo = m_Redo.ToArray();

		SceneEditorSession.Active.UndoSystem.Insert(_Title,
			() =>
			{
				for (int i = undo.Length - 1; i >= 0; i--)
					undo[i]();
			},
			() =>
			{
				foreach (var action in redo)
					action();
			});

		SceneEditorSession.Active.HasUnsavedChanges = true;
	}
}
