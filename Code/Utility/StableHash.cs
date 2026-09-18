using System;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// 64-bit FNV-1a hash that is stable across editor sessions, unlike <see cref="HashCode"/> which is seeded per
/// process. Used to stamp generated meshes with the inputs they were built from, so a scene reopened days later
/// still knows which chunks are current.
/// </summary>
public struct StableHash
{
	private const ulong OffsetBasis = 14695981039346656037UL;
	private const ulong Prime = 1099511628211UL;

	private ulong m_Hash;

	public long Value => unchecked((long)m_Hash);



	public static StableHash Create()
	{
		return new StableHash { m_Hash = OffsetBasis };
	}



	public void Add(int _Value)
	{
		unchecked
		{
			for (int i = 0; i < 4; i++)
			{
				m_Hash ^= (byte)(_Value >> (i * 8));
				m_Hash *= Prime;
			}
		}
	}



	public void Add(long _Value)
	{
		Add(unchecked((int)_Value));
		Add(unchecked((int)(_Value >> 32)));
	}



	public void Add(float _Value)
	{
		Add(BitConverter.SingleToInt32Bits(_Value));
	}



	public void Add(Vector3 _Value)
	{
		Add(_Value.x);
		Add(_Value.y);
		Add(_Value.z);
	}



	public void Add(string _Value)
	{
		if (_Value is not null)
		{
			foreach (char c in _Value)
				Add((int)c);
		}

		// Terminator, so "ab" + "c" never collides with "a" + "bc".
		Add(-1);
	}
}
