using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Which frame a node's position is shown and edited in.
/// </summary>
public enum RoadNodeSpace
{
	/// <summary>
	/// The network's own axes.
	/// </summary>
	Global,

	/// <summary>
	/// The node's road: X along it, Y across it. Height stays height either way.
	/// </summary>
	Local
}

/// <summary>
/// Select and edit what is already there: click a node or a road to select it, drag a node along the ground (or use
/// its move gizmo), drag a node's tangent handles to reshape the roads through it, Ctrl+click a road to insert a
/// node, Delete to remove the selection.
/// </summary>
[Title("Edit")]
[Icon("edit")]
[Group("1")]
[Order(1)]
public class RoadSelectTool : EditorTool
{
	private enum DragKind
	{
		None,
		Node,
		Handle,
		Gizmo
	}

	private const float MinEndDistance = 25.0f;

	public int SelectedNodeId { get; private set; } = -1;

	public int SelectedSegmentId { get; private set; } = -1;

	/// <summary>
	/// Bumped whenever the selection changes, so the sidebar knows to rebuild itself.
	/// </summary>
	public int SelectionVersion { get; private set; }

	public RoadNetwork Network => Root?.Network;

	public RoadNode SelectedNode => Network.IsValid() ? Network.FindNode(SelectedNodeId) : null;

	public RoadSegment SelectedSegment => Network.IsValid() ? Network.FindSegment(SelectedSegmentId) : null;

	/// <summary>
	/// Which frame the selected node's position is shown and edited in, the same choice the move gizmo offers.
	/// Local puts X along the road through the node and Y across it, so a value typed there slides the node down
	/// its own road rather than along a world axis.
	/// </summary>
	[Property, Title("Space")] public RoadNodeSpace NodeSpace { get; set; } = RoadNodeSpace.Global;

	/// <summary>
	/// The selected node's position, in whichever frame <see cref="NodeSpace"/> asks for.
	/// </summary>
	[Property, Title("Position")]
	public Vector3 NodePosition
	{
		get
		{
			var network = Network;

			if (!network.IsValid() || SelectedNode is not { } node)
				return Vector3.Zero;

			return NodeSpace == RoadNodeSpace.Local ? NodeAxes(network, node).Inverse * node.Position : node.Position;
		}
		set
		{
			var network = Network;

			if (!network.IsValid() || SelectedNode is not { } node)
				return;

			network.MoveNode(node.Id, NodeSpace == RoadNodeSpace.Local ? NodeAxes(network, node) * value : value);
		}
	}

	private RoadNetworkTool Root => Manager?.CurrentTool as RoadNetworkTool;



	/// <summary>
	/// The node's own axes in the network's space: forward along the road through it, laid flat so that Z stays
	/// height and a number typed into it does not drag the node along the road's grade. World axes when no road at
	/// the node gives a direction.
	/// </summary>
	private static Rotation NodeAxes(RoadNetwork _Network, RoadNode _Node)
	{
		var segment = _Network.GetSegmentsAt(_Node.Id).FirstOrDefault();

		if (segment is null)
			return Rotation.Identity;

		// The handle points from the node into its road: at the road's end that is against the road's direction.
		Vector3 handle = RoadNetwork.GetHandle(segment, _Node.Id);
		Vector3 forward = (segment.EndNodeId == _Node.Id ? -handle : handle).WithZ(0.0f);

		return forward.IsNearZeroLength ? Rotation.Identity : Rotation.LookAt(forward.Normal, Vector3.Up);
	}

	private DragKind m_Drag;
	private int m_DragSegmentId = -1;
	private Vector3 m_DragStartWorld;
	private Vector3 m_MoveDelta;
	private IDisposable m_UndoScope;



	public override void OnDisabled()
	{
		EndDrag();
	}



	public override Widget CreateToolSidebar()
	{
		return new RoadSelectToolWidget(this);
	}



	public override void OnUpdate()
	{
		var network = Network;

		if (!network.IsValid())
		{
			EndDrag();
			ClearSelection();
			return;
		}

		// Undo can remove whatever we had selected.
		if (SelectedNodeId >= 0 && network.FindNode(SelectedNodeId) is null)
			ClearSelection();

		if (SelectedSegmentId >= 0 && network.FindSegment(SelectedSegmentId) is null)
			ClearSelection();

		if (m_Drag is DragKind.Node or DragKind.Handle)
		{
			UpdateDirectDrag(network);
			DrawSelection(network);
			return;
		}

		bool canPick = Gizmo.HasMouseFocus && !Gizmo.HasHovered && !Gizmo.Pressed.Any;
		RoadNode hoverNode = null;
		RoadSegmentPick? hoverSegment = null;
		int hoverHandleSegment = -1;

		if (canPick)
		{
			var ray = Gizmo.CurrentRay;

			if (SelectedNode is { } selected)
				hoverHandleSegment = PickHandle(network, selected, ray);

			if (hoverHandleSegment < 0)
				hoverNode = RoadPicker.PickNode(network, ray);

			if (hoverHandleSegment < 0 && hoverNode is null)
				hoverSegment = RoadPicker.PickSegment(network, ray);
		}

		DrawHover(network, hoverNode, hoverSegment);
		DrawSelection(network, hoverHandleSegment);

		if (canPick && Gizmo.WasLeftMousePressed)
			OnPress(network, hoverNode, hoverSegment, hoverHandleSegment);

		if (SelectedNode is { } node)
			UpdateNodeGizmo(network, node);
	}



	public void Select(RoadNode _Node)
	{
		SetSelection(_Node?.Id ?? -1, -1);
	}



	public void Select(RoadSegment _Segment)
	{
		SetSelection(-1, _Segment?.Id ?? -1);
	}



	public void ClearSelection()
	{
		SetSelection(-1, -1);
	}



	private void SetSelection(int _NodeId, int _SegmentId)
	{
		if (SelectedNodeId == _NodeId && SelectedSegmentId == _SegmentId)
			return;

		SelectedNodeId = _NodeId;
		SelectedSegmentId = _SegmentId;
		SelectionVersion++;
	}



	public void DeleteSelection()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		if (SelectedNode is { } node)
		{
			// A node in the middle of a road is dissolved so the road carries on in one piece; anything else
			// (a road end, a junction) takes its roads with it.
			bool passThrough = network.GetDegree(node.Id) == 2;

			using (SceneEditorSession.Active.UndoScope(passThrough ? "Dissolve Node" : "Delete Node").WithComponentChanges(network).Push())
			{
				if (!passThrough || network.DissolveNode(node.Id) is null)
					network.RemoveNode(node.Id);
			}
		}
		else if (SelectedSegment is { } segment)
		{
			using (SceneEditorSession.Active.UndoScope("Delete Road").WithComponentChanges(network).Push())
				network.RemoveSegment(segment.Id);
		}

		ClearSelection();
	}



	/// <summary>
	/// Sidebar edits of the selected node land on the node object directly; this pushes them through the network
	/// so tangents and chunks follow.
	/// </summary>
	public void OnNodeEdited(RoadNode _Node, string _PropertyName)
	{
		var network = Network;

		if (!network.IsValid() || _Node is null)
			return;

		if (_PropertyName == nameof(RoadNode.AutoTangents))
		{
			network.SetAutoTangents(_Node.Id, _Node.AutoTangents);
			return;
		}

		// The value is already written on the node; only the consequences are left to apply.
		if (_PropertyName == nameof(RoadNode.Position))
			network.RefreshTangentsAround(_Node.Id);

		network.MarkDirty();
	}



	public void OnSegmentEdited()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		// Turning a road into a ramp (or back) changes how the roads at its ends flow through their nodes.
		if (SelectedSegment is { } segment)
		{
			network.RefreshTangentsAround(segment.StartNodeId);
			network.RefreshTangentsAround(segment.EndNodeId);
		}

		network.MarkDirty();
	}



	private void OnPress(RoadNetwork _Network, RoadNode _HoverNode, RoadSegmentPick? _HoverSegment, int _HoverHandleSegment)
	{
		if (_HoverHandleSegment >= 0)
		{
			m_Drag = DragKind.Handle;
			m_DragSegmentId = _HoverHandleSegment;
			return;
		}

		if (_HoverNode is not null)
		{
			Select(_HoverNode);
			m_Drag = DragKind.Node;
			m_DragStartWorld = _Network.WorldTransform.PointToWorld(_HoverNode.Position);
			return;
		}

		if (_HoverSegment is { } pick)
		{
			if (Gizmo.IsCtrlPressed)
			{
				RoadNode inserted;

				using (SceneEditorSession.Active.UndoScope("Insert Node").WithComponentChanges(_Network).Push())
					inserted = _Network.SplitSegment(pick.Segment.Id, pick.T, MinEndDistance);

				Select(inserted);
			}
			else
			{
				Select(pick.Segment);
			}

			return;
		}

		ClearSelection();
	}



	/// <summary>
	/// A node pressed directly slides along the ground under the cursor; a handle slides on the horizontal plane
	/// through its current position, so a road's vertical profile is only changed on purpose (through the gizmo).
	/// </summary>
	private void UpdateDirectDrag(RoadNetwork _Network)
	{
		if (!Gizmo.IsLeftMouseDown)
		{
			EndDrag();
			return;
		}

		// A press that never travels is a click, not a drag: don't record an undo step for it.
		if (Vector2.Distance(Gizmo.CursorPosition, Gizmo.Pressed.CursorPosition) < 2.0f && m_UndoScope is null)
			return;

		var transform = _Network.WorldTransform;

		if (m_Drag == DragKind.Node && SelectedNode is { } node)
		{
			if (GroundHit(_Network, transform.PointToWorld(node.Position)) is not Vector3 hit)
				return;

			m_UndoScope ??= SceneEditorSession.Active.UndoScope("Move Node").WithComponentChanges(_Network).Push();
			_Network.MoveNode(node.Id, transform.PointToLocal(hit));
		}
		else if (m_Drag == DragKind.Handle && SelectedNode is { } handleNode && _Network.FindSegment(m_DragSegmentId) is { } segment)
		{
			Vector3 handleWorld = transform.PointToWorld(handleNode.Position + RoadNetwork.GetHandle(segment, handleNode.Id));
			var plane = new Plane(handleWorld, Vector3.Up);

			if (plane.Trace(Gizmo.CurrentRay, true) is not Vector3 hit)
				return;

			m_UndoScope ??= SceneEditorSession.Active.UndoScope("Move Tangent").WithComponentChanges(_Network).Push();

			// Alt breaks the mirror so a pass-through node can be turned into a corner.
			Vector3 handle = transform.PointToLocal(hit) - handleNode.Position;
			_Network.SetHandle(segment.Id, handleNode.Id, handle, !Gizmo.IsAltPressed);
		}
		else
		{
			EndDrag();
		}
	}



	/// <summary>
	/// The standard move gizmo on the selected node. Its centre square drags along the ground (the raycast hook),
	/// the arrows move on world axes, or in Local space (the editor's Global/Local toggle) along and across the
	/// road through the node, with grid snapping in whichever frame the arrows are in.
	/// </summary>
	private void UpdateNodeGizmo(RoadNetwork _Network, RoadNode _Node)
	{
		if (m_Drag is DragKind.Node or DragKind.Handle)
			return;

		var transform = _Network.WorldTransform;
		Vector3 nodeWorld = transform.PointToWorld(_Node.Position);
		var handleRotation = Gizmo.Settings.GlobalSpace ? Rotation.Identity : NodeFrame(_Network, _Node);

		using (Gizmo.Scope("roadtool2_node_gizmo", new Transform(nodeWorld)))
		{
			Gizmo.Hitbox.DepthBias = 0.01f;

			Vector3? surfaceHit = null;

			bool moved = Gizmo.Control.Position("position", Vector3.Zero, out Vector3 delta, handleRotation, centerRaycast: () =>
			{
				surfaceHit = GroundHit(_Network, nodeWorld);
				return surfaceHit;
			});

			if (moved)
			{
				if (m_Drag != DragKind.Gizmo)
				{
					m_Drag = DragKind.Gizmo;
					m_DragStartWorld = nodeWorld;
					m_MoveDelta = Vector3.Zero;
					m_UndoScope ??= SceneEditorSession.Active.UndoScope("Move Node").WithComponentChanges(_Network).Push();
				}

				Vector3 target;

				if (surfaceHit is Vector3 hit)
				{
					target = hit;
				}
				else
				{
					m_MoveDelta += delta;
					target = Gizmo.Snap(m_DragStartWorld, m_MoveDelta, handleRotation);
				}

				_Network.MoveNode(_Node.Id, transform.PointToLocal(target));
			}
		}

		if (m_Drag == DragKind.Gizmo && !Gizmo.IsLeftMouseDown)
			EndDrag();
	}



	/// <summary>
	/// The node's own frame for Local space: forward is the way the road through it runs (its first road's
	/// direction, which at a smooth pass-through node is the same from either side), so the arrows point along
	/// and across the road. World axes when no road at the node gives a direction.
	/// </summary>
	private static Rotation NodeFrame(RoadNetwork _Network, RoadNode _Node)
	{
		var segment = _Network.GetSegmentsAt(_Node.Id).FirstOrDefault();

		if (segment is null)
			return Rotation.Identity;

		// The handle points from the node into its road: at the road's end that is against the road's direction.
		Vector3 handle = RoadNetwork.GetHandle(segment, _Node.Id);
		Vector3 forward = _Network.WorldTransform.NormalToWorld(segment.EndNodeId == _Node.Id ? -handle : handle);

		if (forward.IsNearZeroLength || MathF.Abs(forward.Normal.z) > 0.99f)
			return Rotation.Identity;

		return Rotation.LookAt(forward.Normal, Vector3.Up);
	}



	private void EndDrag()
	{
		m_Drag = DragKind.None;
		m_DragSegmentId = -1;
		m_UndoScope?.Dispose();
		m_UndoScope = null;
	}



	/// <summary>
	/// Where the cursor ray meets the ground, ignoring the road meshes themselves (the node sits right on them).
	/// Falls back to a horizontal plane through the point being dragged when nothing is under the cursor.
	/// </summary>
	private Vector3? GroundHit(RoadNetwork _Network, Vector3 _Fallback)
	{
		var trace = Trace.WithoutTags(RoadNetwork.ChunkTag).Run();

		if (trace.Hit)
			return trace.EndPosition;

		return new Plane(_Fallback, Vector3.Up).Trace(Gizmo.CurrentRay, true);
	}



	/// <summary>
	/// The segment whose tangent handle at the selected node is under the ray, or -1.
	/// </summary>
	private static int PickHandle(RoadNetwork _Network, RoadNode _Node, Ray _Ray)
	{
		var transform = _Network.WorldTransform;
		int best = -1;
		float bestDistance = float.MaxValue;

		foreach (var segment in _Network.GetSegmentsAt(_Node.Id))
		{
			Vector3 world = transform.PointToWorld(_Node.Position + RoadNetwork.GetHandle(segment, _Node.Id));
			var sphere = new Sphere(world, RoadPicker.NodeRadius(world) * 0.7f);

			if (!sphere.Trace(_Ray, float.MaxValue, out float distance) || distance >= bestDistance)
				continue;

			best = segment.Id;
			bestDistance = distance;
		}

		return best;
	}



	private static void DrawHover(RoadNetwork _Network, RoadNode _HoverNode, RoadSegmentPick? _HoverSegment)
	{
		var transform = _Network.WorldTransform;

		using (Gizmo.Scope("roadtool2_hover"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 2.0f;
			Gizmo.Draw.Color = Color.Cyan.WithAlpha(0.8f);

			if (_HoverNode is not null)
				RoadGizmos.DrawNode(transform.PointToWorld(_HoverNode.Position), false);

			if (_HoverSegment is { } pick && _Network.TryGetCurve(pick.Segment, out var curve))
			{
				RoadGizmos.DrawRoadOutline(curve, _Network.WidthOf(pick.Segment), transform);

				// Ctrl turns the click into "insert a node here": show where.
				if (Gizmo.IsCtrlPressed)
				{
					Gizmo.Draw.Color = Color.Orange;
					RoadGizmos.DrawNode(transform.PointToWorld(pick.LocalPoint), true);
				}
			}
		}
	}



	private void DrawSelection(RoadNetwork _Network, int _HoverHandleSegment = -1)
	{
		var transform = _Network.WorldTransform;

		using (Gizmo.Scope("roadtool2_selection"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 2.0f;

			if (SelectedSegment is { } segment && _Network.TryGetCurve(segment, out var curve))
			{
				Gizmo.Draw.Color = Color.Yellow;
				RoadGizmos.DrawRoadOutline(curve, _Network.WidthOf(segment), transform);
			}

			if (SelectedNode is not { } node)
				return;

			Vector3 nodeWorld = transform.PointToWorld(node.Position);

			Gizmo.Draw.Color = Color.Yellow;
			RoadGizmos.DrawNode(nodeWorld, true);

			// Tangent handles: one per road leaving the node.
			foreach (var attached in _Network.GetSegmentsAt(node.Id))
			{
				Vector3 handleWorld = transform.PointToWorld(node.Position + RoadNetwork.GetHandle(attached, node.Id));
				bool active = attached.Id == _HoverHandleSegment || (m_Drag == DragKind.Handle && attached.Id == m_DragSegmentId);

				Gizmo.Draw.Color = node.AutoTangents ? Color.White.WithAlpha(0.5f) : Color.White;
				Gizmo.Draw.LineThickness = 1.0f;
				Gizmo.Draw.Line(nodeWorld, handleWorld);

				Gizmo.Draw.Color = active ? Color.Cyan : (node.AutoTangents ? Color.White.WithAlpha(0.6f) : Color.White);
				Gizmo.Draw.SolidSphere(handleWorld, RoadPicker.NodeRadius(handleWorld) * 0.45f);
			}
		}
	}
}



file class RoadSelectToolWidget : ToolSidebarWidget
{
	private readonly RoadSelectTool m_Tool;
	private int m_ShownVersion = -1;
	private IDisposable m_UndoScope;



	public RoadSelectToolWidget(RoadSelectTool _Tool)
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
		AddTitle("Edit Road", "edit");

		var network = m_Tool.Network;

		if (network.IsValid() && m_Tool.SelectedNode is { } node)
			BuildNodeSection(network, node);
		else if (network.IsValid() && m_Tool.SelectedSegment is { } segment)
			BuildSegmentSection(network, segment);
		else
			AddGroup("Selection").Add(new Label("Click a node or a road to select it."));

		AddShortcuts(
			("Select", "LMB"),
			("Drag Node / Tangent", "LMB Drag"),
			("Straight Tangent (no mirror)", "ALT + Drag"),
			("Insert Node On Road", "CTRL + LMB"),
			("Delete Selection", "DEL"),
			("Deselect", "ESC")
		);

		Layout.AddStretchCell();
	}



	private void BuildNodeSection(RoadNetwork _Network, RoadNode _Node)
	{
		var group = AddGroup($"Node #{_Node.Id}");
		var serialized = _Node.GetSerialized();

		serialized.OnPropertyStartEdit += _ => BeginEdit("Edit Node");
		serialized.OnPropertyFinishEdit += _ => EndEdit();
		serialized.OnPropertyChanged += property => m_Tool.OnNodeEdited(_Node, property?.Name);

		// The position is read off the tool rather than the node, so it can be shown in the node's own frame as
		// well as the network's; its setter moves the node either way.
		var tool = m_Tool.GetSerialized();

		tool.OnPropertyStartEdit += _ => BeginEdit("Move Node");
		tool.OnPropertyFinishEdit += _ => EndEdit();

		SidebarRows.Add(group, tool.GetProperty(nameof(RoadSelectTool.NodeSpace)));
		SidebarRows.AddVector(group, tool.GetProperty(nameof(RoadSelectTool.NodePosition)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadNode.AutoTangents)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadNode.CornerRadius)));
		group.Add(new Label($"Roads at this node: {_Network.GetDegree(_Node.Id)}"));

		var row = group.AddRow();
		row.Spacing = 4;
		row.Add(new Button("Delete Node", "delete") { Clicked = m_Tool.DeleteSelection });
	}



	private void BuildSegmentSection(RoadNetwork _Network, RoadSegment _Segment)
	{
		var group = AddGroup($"Road #{_Segment.Id}");
		var serialized = _Segment.GetSerialized();

		serialized.OnPropertyStartEdit += _ => BeginEdit("Edit Road");
		serialized.OnPropertyFinishEdit += _ => EndEdit();
		serialized.OnPropertyChanged += _ => m_Tool.OnSegmentEdited();

		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadSegment.Profile)));
		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadSegment.Bridge)));
		SidebarRows.AddWide(group, serialized.GetProperty(nameof(RoadSegment.Props)));
		SidebarRows.Add(group, serialized.GetProperty(nameof(RoadSegment.Width)));

		float length = _Network.GetCurveTable(_Segment)?.Length ?? 0.0f;
		group.Add(new Label($"Length: {length:0} units"));

		var row = group.AddRow();
		row.Spacing = 4;
		row.Add(new Button("Delete Road", "delete") { Clicked = m_Tool.DeleteSelection });

		// How the edges run: extra width per side at each end (a lane appearing over a taper), and the ramp
		// treatment for a road leaving another from its side.
		var shape = AddGroup("Edges");

		SidebarRows.Add(shape, serialized.GetProperty(nameof(RoadSegment.LeftExtraStart)));
		SidebarRows.Add(shape, serialized.GetProperty(nameof(RoadSegment.LeftExtraEnd)));
		SidebarRows.Add(shape, serialized.GetProperty(nameof(RoadSegment.RightExtraStart)));
		SidebarRows.Add(shape, serialized.GetProperty(nameof(RoadSegment.RightExtraEnd)));
		SidebarRows.Add(shape, serialized.GetProperty(nameof(RoadSegment.Ramp)));
	}



	private void BeginEdit(string _Name)
	{
		var network = m_Tool.Network;

		if (!network.IsValid())
			return;

		m_UndoScope ??= SceneEditorSession.Active.UndoScope(_Name).WithComponentChanges(network).Push();
	}



	private void EndEdit()
	{
		m_UndoScope?.Dispose();
		m_UndoScope = null;
	}



	[Shortcut("roadtool2.delete-selection", "DEL", typeof(SceneViewWidget))]
	private void DeleteSelection()
	{
		m_Tool.DeleteSelection();
	}



	[Shortcut("roadtool2.deselect", "ESC", typeof(SceneViewWidget))]
	private void Deselect()
	{
		m_Tool.ClearSelection();
	}
}
