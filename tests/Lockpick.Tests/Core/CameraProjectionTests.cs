using Lockpick.Core.Maps;

namespace Lockpick.Tests.Core;

public class CameraProjectionTests
{
    // Room 115, camera 2, from Jill's room file (GOG English).
    private static readonly MapCamera Room115 = new(2, 1890, 5382, 6912, 4554, Y: -648, TargetY: -2484, Fov: 188);

    [Fact]
    public void The_point_the_camera_looks_at_is_the_middle_of_the_picture()
    {
        var (x, y) = CameraProjection.Project(Room115, Room115.TargetX, Room115.TargetY, Room115.TargetZ)!.Value;
        Assert.Equal(160, x, 6);
        Assert.Equal(120, y, 6);
    }

    [Fact]
    public void The_players_feet_land_where_the_game_drew_them()
    {
        // Live 2026-10-27: Jill at (4802, 0, 3066); her boots were at about (279.8, 233) of the 320x240 picture.
        var (x, y) = CameraProjection.Project(Room115, 4802, 0, 3066)!.Value;
        Assert.InRange(x, 279, 281);
        Assert.InRange(y, 232, 235);
    }

    [Fact]
    public void Nothing_behind_the_camera_or_without_a_field_of_view_is_drawn()
    {
        Assert.Null(CameraProjection.Project(Room115, Room115.X - 3000, 0, Room115.Z));
        Assert.Null(CameraProjection.Project(Room115 with { Fov = 0 }, Room115.TargetX, Room115.TargetY, Room115.TargetZ));
    }
}
