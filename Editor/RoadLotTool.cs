using System;
using System.Linq;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Parking lots: click the loose end of a road and drag out a rectangle to lay a lot there; the road opens into it
/// and the lot is surfaced, kerbed and marked out in bays. Click a lot to select it and edit its size, its road
/// type and its parking layout.
/// </summary>
[Title("Parking")]
[Icon("local_parking")]
[Group("1")]
[Order(4)]
public class RoadLotTool : EditorTool
{
	/// <summary>
	/// The road type new lots are surfaced as. Empty uses the network's default.
	/// </summary>
	[Property, Title("Profile")] public RoadProfile Profile { get; set; }

	/// <summary>
	/// How new lots are laid out inside.
	/// </summary>
	[Property, Title("Parking")] public ParkingStyle Parking { get; set; }

	public int SelectedLotId { get; private set; } = -1;

	/// <summary>
	/// Bumped whenever the selection changes, so the sidebar knows to rebuild itself.
	/// </summary>
	public int SelectionVersion { get; private set; }

	public RoadNetwork Network => Root?.Network;

	public RoadLot SelectedLot => Network.IsValid() ? Network.FindLot(SelectedLotId) : null;

	private RoadNetworkTool Root => Manager?.CurrentTool as RoadNetworkTool;

	/// <summary>
	/// The node the lot being dragged grows from, or -1 when nothing is being dragged.
	/// </summary>
	private int m_FromNodeId = -1;

	private float m_Width;
	private float m_Depth;



	public override void OnDisabled()
	{
		m_FromNodeId = -1;
	}



	public override Widget CreateToolSidebar()
	{
		return new RoadLotToolWidget(this);
	}



	public override void OnUpdate()
	{
		var network = Network;

		if (!network.IsValid())
		{
			m_FromNodeId = -1;
			return;
		}

		if (SelectedLotId >= 0 && network.FindLot(SelectedLotId) is null)
			Select(null);

		DrawLots(network);

		if (m_FromNodeId >= 0)
		{
			UpdateDrag(network);
			return;
		}

		if (!Gizmo.HasMouseFocus || Gizmo.HasHovered)
			return;

		var node = FreeNodeUnderCursor(network);
		var joinable = node is null ? null : LotNear(network, node);

		if (node is not null)
			DrawNodeHint(network, node, joinable);

		if (!Gizmo.WasLeftMousePressed)
			return;

		if (node is not null && joinable is not null)
		{
			// A road end close to a lot joins it as another way in, rather than starting a lot of its own.
			using (SceneEditorSession.Active.UndoScope("Join Road To Parking Lot").WithComponentChanges(network).Push())
				network.JoinOpening(joinable.Id, node.Id);

			Select(joinable);

			return;
		}

		if (node is not null)
		{
			m_FromNodeId = node.Id;
			m_Width = 0.0f;
			m_Depth = 0.0f;

			return;
		}

		Select(LotUnderCursor(network));
	}



	/// <summary>
	/// While dragging: the lot reaches from the node's edge out to the cursor, as wide either side of the node as
	/// the cursor is across from it.
	/// </summary>
	private void UpdateDrag(RoadNetwork _Network)
	{
		var node = _Network.FindNode(m_FromNodeId);

		if (node is null || Gizmo.WasRightMousePressed)
		{
			m_FromNodeId = -1;
			return;
		}

		var transform = _Network.WorldTransform;
		Vector3 nodeWorld = transform.PointToWorld(node.Position);
		var plane = new Plane(nodeWorld, Vector3.Up);

		if (plane.Trace(Gizmo.CurrentRay, true) is not Vector3 hit)
			return;

		Vector3 away = AwayFromRoad(_Network, node);
		Vector3 across = Vector3.Cross(Vector3.Up, away).Normal;
		Vector3 offset = transform.PointToLocal(hit) - node.Position;

		m_Depth = MathF.Max(RoadLot.MinSize, Vector3.Dot(offset, away));
		m_Width = MathF.Max(RoadLot.MinSize, MathF.Abs(Vector3.Dot(offset, across)) * 2.0f);

		DrawPreview(_Network, node, away, across);

		if (!Gizmo.WasLeftMousePressed)
			return;

		RoadLot lot;

		using (SceneEditorSession.Active.UndoScope("Add Parking Lot").WithComponentChanges(_Network).Push())
			lot = _Network.AddLot(node.Id, m_Width, m_Depth, Profile, Parking);

		m_FromNodeId = -1;
		Select(lot);
	}



	public void Select(RoadLot _Lot)
	{
		int id = _Lot?.Id ?? -1;

		if (id == SelectedLotId)
			return;

		SelectedLotId = id;
		SelectionVersion++;
	}



	/// <summary>
	/// The sidebar edited the selected lot directly; push it through the network so its entrances and pieces follow.
	/// </summary>
	public void OnLotEdited()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		network.SnapOpenings(SelectedLot);
		network.MarkDirty();
	}



	public void DeleteSelection()
	{
		var network = Network;

		if (!network.IsValid() || SelectedLot is not { } lot)
			return;

		using (SceneEditorSession.Active.UndoScope("Delete Parking Lot").WithComponentChanges(network).Push())
			network.RemoveLot(lot.Id);

		Select(null);
	}



	/// <summary>
	/// Which way a lot laid at a node reaches: on down the road, away from where it came from.
	/// </summary>
	private static Vector3 AwayFromRoad(RoadNetwork _Network, RoadNode _Node)
	{
		var segment = _Network.GetSegmentsAt(_Node.Id).FirstOrDefault();

		if (segment is null)
			return Vector3.Forward;

		Vector3 handle = RoadNetwork.GetHandle(segment, _Node.Id).WithZ(0.0f);

		return handle.IsNearZeroLength ? Vector3.Forward : -handle.Normal;
	}



	/// <summary>
	/// A loose road end under the cursor: one road at it, and no lot of its own yet.
	/// </summary>
	private RoadNode FreeNodeUnderCursor(RoadNetwork _Network)
	{
		var node = RoadPicker.PickNode(_Network, Gizmo.CurrentRay);

		if (node is null || _Network.GetDegree(node.Id) > 1 || _Network.LotAtOpening(node.Id) is not null)
			return null;

		return node;
	}



	/// <summary>
	/// The lot the cursor is over, from the ground point under it.
	/// </summary>
	private RoadLot LotUnderCursor(RoadNetwork _Network)
	{
		var transform = _Network.WorldTransform;

		foreach (var lot in _Network.Lots)
		{
			var plane = new Plane(transform.PointToWorld(lot.Centre), transform.NormalToWorld(Vector3.Up));

			if (plane.Trace(Gizmo.CurrentRay, true) is not Vector3 hit)
				continue;

			Vector2 local = lot.Local(transform.PointToLocal(hit));

			if (MathF.Abs(local.x) <= lot.HalfWidth && MathF.Abs(local.y) <= lot.HalfDepth)
				return lot;
		}

		return null;
	}



	private void DrawLots(RoadNetwork _Network)
	{
		var transform = _Network.WorldTransform;

		using (Gizmo.Scope("roadtool2_lots"))
		{
			Gizmo.Draw.IgnoreDepth = true;

			foreach (var lot in _Network.Lots)
			{
				bool selected = lot.Id == SelectedLotId;

				Gizmo.Draw.LineThickness = selected ? 3.0f : 1.5f;
				Gizmo.Draw.Color = (selected ? Color.Orange : Color.White).WithAlpha(selected ? 0.9f : 0.4f);

				DrawOutline(lot, transform, _Network);
			}
		}
	}



	private static void DrawOutline(RoadLot _Lot, in Transform _Transform, RoadNetwork _Network = null)
	{
		var outline = RoadLotOutline.Build(_Lot, _Network?.OpeningsOf(_Lot));

		for (int i = 0; i < outline.Count; i++)
		{
			int next = (i + 1) % outline.Count;

			Gizmo.Draw.Line(_Transform.PointToWorld(outline.Points[i]), _Transform.PointToWorld(outline.Points[next]));
		}
	}



	/// <summary>
	/// A lot near enough to a road's loose end to join it to: one whose edge the end is on or within a road's
	/// width of.
	/// </summary>
	private static RoadLot LotNear(RoadNetwork _Network, RoadNode _Node)
	{
		float reach = MathF.Max(200.0f, _Network.GetSegmentsAt(_Node.Id).FirstOrDefault()?.Width ?? 0.0f);

		foreach (var lot in _Network.Lots)
		{
			Vector2 local = lot.Local(_Node.Position);
			float outside = MathF.Max(MathF.Abs(local.x) - lot.HalfWidth, MathF.Abs(local.y) - lot.HalfDepth);

			if (outside <= reach)
				return lot;
		}

		return null;
	}



	private void DrawNodeHint(RoadNetwork _Network, RoadNode _Node, RoadLot _Joinable)
	{
		Vector3 world = _Network.WorldTransform.PointToWorld(_Node.Position);

		using (Gizmo.Scope("roadtool2_lot_hint"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 2.0f;
			Gizmo.Draw.Color = Color.Cyan.WithAlpha(0.9f);
			RoadGizmos.DrawNode(world, true);
			Gizmo.Draw.ScreenText(_Joinable is null ? "Drag out a parking lot" : $"Join lot #{_Joinable.Id}", world, new Vector2(12.0f, -12.0f), size: 12.0f);
		}
	}



	private void DrawPreview(RoadNetwork _Network, RoadNode _Node, Vector3 _Away, Vector3 _Across)
	{
		var transform = _Network.WorldTransform;
		Vector3 centre = _Node.Position + _Away * (m_Depth * 0.5f);

		var preview = new RoadLot
		{
			Centre = centre,
			Yaw = _Away.EulerAngles.yaw,
			Width = m_Width,
			Depth = m_Depth
		};

		using (Gizmo.Scope("roadtool2_lot_preview"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 2.0f;
			Gizmo.Draw.Color = Color.Cyan.WithAlpha(0.9f);

			DrawOutline(preview, transform);

			Gizmo.Draw.ScreenText($"{m_Width:0} x {m_Depth:0}", transform.PointToWorld(centre), new Vector2(0.0f, 0.0f), size: 12.0f);
		}
	}
}



file class RoadLotToolWidget : ToolSidebarWidget
{
	private readonly RoadLotTool m_Tool;
	private int m_ShownVersion = -1;
	private IDisposable m_UndoScope;



	public RoadLotToolWidget(RoadLotTool _Tool)
	{
		m_Tool = _Tool;

		Rebuild();
	}



	[EditorEvent.Frame]
	public void Frame()
	{
		if (m_Tool.SelectionVersion != m_ShownVersion)
			Rebuild();
	}



	private void Rebuild()
	{
		m_ShownVersion = m_Tool.SelectionVersion;

		Layout.Clear(true);
		AddTitle("Parking", "local_parking");

		var tool = m_Tool.GetSerialized();
		var defaults = AddGroup("New Lots");

		SidebarRows.AddWide(defaults, tool.GetProperty(nameof(RoadLotTool.Profile)));
		SidebarRows.AddWide(defaults, tool.GetProperty(nameof(RoadLotTool.Parking)));

		if (m_Tool.SelectedLot is { } lot)
		{
			var group = AddGroup($"Lot #{lot.Id}");
			var serialized = lot.GetSerialized();

			serialized.OnPropertyStartEdit += _ => BeginEdit();
			serialized.OnPropertyFinishEdit += _ => EndEdit();
			serialized.OnPropertyChanged += _ => m_Tool.OnLotEdited();

			SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadLot.Profile)));
			SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadLot.Parking)));
			SidebarRows.Add(group, serialized.GetProperty(nameof(RoadLot.Width)));
			SidebarRows.Add(group, serialized.GetProperty(nameof(RoadLot.Depth)));
			SidebarRows.Add(group, serialized.GetProperty(nameof(RoadLot.Yaw)));
			SidebarRows.Add(group, serialized.GetProperty(nameof(RoadLot.CornerRadius)));
			SidebarRows.Add(group, serialized.GetProperty(nameof(RoadLot.EntranceRadius)));
			SidebarRows.AddVector(group, serialized.GetProperty(nameof(RoadLot.Centre)));

			group.Add(new Label($"Entrances: {lot.Openings.Count}"));

			var row = group.AddRow();
			row.Spacing = 4;
			row.Add(new Button("Delete Lot", "delete") { Clicked = m_Tool.DeleteSelection });
		}
		else
		{
			AddGroup("Lot").Add(new Label("Click the loose end of a road and drag to lay a parking lot there, or click a lot to edit it.") { WordWrap = true });
		}

		AddShortcuts(
			("Start A Lot", "LMB on road end"),
			("Place It", "LMB"),
			("Cancel", "RMB"),
			("Join A Road To A Lot", "LMB on end near lot"),
			("Select A Lot", "LMB on lot")
		);

		Layout.AddStretchCell();
	}



	private void BeginEdit()
	{
		var network = m_Tool.Network;

		if (!network.IsValid())
			return;

		m_UndoScope ??= SceneEditorSession.Active.UndoScope("Edit Parking Lot").WithComponentChanges(network).Push();
	}



	private void EndEdit()
	{
		m_UndoScope?.Dispose();
		m_UndoScope = null;
	}
}
