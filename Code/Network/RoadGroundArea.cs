using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// A filled area of road surface the ground is shaped to as a whole (a junction, a parking lot, a road's rounded dead
/// end), in network space lifted to the road surface: its centre and the outline its surface fans out to, how far a
/// pavement reaches out round it and how far above the outline that pavement's top sits, how high and how wide the
/// ground banks up round it (see <see cref="RoadProfile.BankHeight"/>), and the roads it belongs to (a rounded end its
/// own road, a junction its arms, a parking lot none). The banks are the same all round (a banked road's rounded
/// end) unless <see cref="VertexBanks"/> gives them for each point of the outline, as height and width, to be taken
/// along its edges between them (a junction, banked along the stretches of outline its banked roads own);
/// <see cref="BankHeight"/> and <see cref="BankWidth"/> are then the most they reach anywhere.
/// </summary>
public readonly record struct RoadGroundArea(Vector3 Centre, IReadOnlyList<Vector3> Polygon, float PavementReach, float PavementTop, float BankHeight, float BankWidth, IReadOnlyList<RoadSegment> Roads,
	IReadOnlyList<Vector2> VertexBanks = null);
