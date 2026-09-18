using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates the surface of a junction: the layout's polygon fanned out from the node. Every triangle shares the
/// centre, so each one is planar and none can be dropped by the mesh builder, whatever the arms' heights do.
/// </summary>
internal static class RoadJunctionBuilder
{
	public static PolygonMesh Build(RoadJunctionLayout _Layout, float _TextureRepeat, Material _Material)
	{
		var mesh = new PolygonMesh();
		var polygon = _Layout.Polygon;

		if (polygon.Count < 3)
			return mesh;

		var centre = mesh.AddVertices(_Layout.Centre)[0];
		var ring = mesh.AddVertices(polygon.ToArray());
		Vector2 centreUv = PlanarUv(_Layout.Centre, _TextureRepeat);

		for (int i = 0; i < polygon.Count; i++)
		{
			int next = (i + 1) % polygon.Count;

			MeshUtility.AddTexturedTriangle(mesh, _Material, centre, ring[i], ring[next],
				centreUv, PlanarUv(polygon[i], _TextureRepeat), PlanarUv(polygon[next], _TextureRepeat));
		}

		return mesh;
	}



	/// <summary>
	/// Junctions have no single direction to run a texture along, so they use a top-down projection.
	/// </summary>
	private static Vector2 PlanarUv(Vector3 _Position, float _TextureRepeat)
	{
		return new Vector2(_Position.x, _Position.y) / _TextureRepeat;
	}
}
