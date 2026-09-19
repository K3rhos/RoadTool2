using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Root tool of Road Tool 2.0. Resolves the network being edited and draws the graph overlay; the actual
/// interaction lives in the sub tools.
/// </summary>
[EditorTool]
[Title("Road Tool 2.0")]
[Icon("add_road")]
[Alias("roadtool2")]
[Group("Tools")]
public class RoadNetworkTool : EditorTool
{
	/// <summary>
	/// The network edits go to. Null until the first road is drawn in a scene without one.
	/// </summary>
	public RoadNetwork Network { get; private set; }



	public override IEnumerable<EditorTool> GetSubtools()
	{
		yield return new RoadDrawTool();
		yield return new RoadSelectTool();
		yield return new RoadLaneTool();
		yield return new RoadTerrainTool();
	}



	public override void OnEnabled()
	{
		AllowGameObjectSelection = false;
		ResolveNetwork();
	}



	public override void OnSelectionChanged()
	{
		ResolveNetwork();
	}



	public override void OnUpdate()
	{
		if (!Network.IsValid())
			ResolveNetwork();

		DrawGraph();
	}



	/// <summary>
	/// Prefers a selected network (or a selected chunk's network), else the first one in the scene.
	/// </summary>
	public void ResolveNetwork()
	{
		var selected = Selection.OfType<GameObject>()
			.Select(gameObject => gameObject.Components.Get<RoadNetwork>() ?? gameObject.Parent?.Components.Get<RoadNetwork>())
			.FirstOrDefault(network => network.IsValid());

		Network = selected ?? Scene.GetAllComponents<RoadNetwork>().FirstOrDefault(network => network.IsValid());
	}



	/// <summary>
	/// Creates a fresh network at the scene root. Call it inside an undo scope that records GameObject creations.
	/// </summary>
	public RoadNetwork CreateNetwork()
	{
		var gameObject = Scene.CreateObject();
		gameObject.Name = "Road Network";

		Network = gameObject.AddComponent<RoadNetwork>();

		return Network;
	}



	private void DrawGraph()
	{
		if (!Network.IsValid())
			return;

		var transform = Network.WorldTransform;

		using (Gizmo.Scope("roadtool2_graph"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 1.0f;
			Gizmo.Draw.Color = Color.White.WithAlpha(0.35f);

			foreach (var segment in Network.Segments)
			{
				if (Network.TryGetCurve(segment, out var curve))
					RoadGizmos.DrawCurve(curve, transform);
			}

			Gizmo.Draw.Color = Color.White.WithAlpha(0.6f);

			foreach (var node in Network.Nodes)
				RoadGizmos.DrawNode(transform.PointToWorld(node.Position), false);
		}
	}
}
