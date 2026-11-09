using System.Buffers.Binary;
using System.Net;
using System.Text;
using YawVR.Link.Protocol;

namespace YawVR.Link.Tests;

public class ProtocolTests
{
    private static readonly IPEndPoint Anywhere = new(IPAddress.Loopback, 1);

    [Fact]
    public void CheckIn_matches_the_documented_example()
    {
        // Emulator/docs/protocol.md: port 5000, game "Demo".
        Assert.Equal(Convert.FromHexString("300000138844656D6F"), Packets.CheckIn(5000, "Demo"));
    }

    [Fact]
    public void Tilt_limits_use_GameLink_order_backward_forward_roll()
    {
        var bytes = Packets.SetTiltLimits(new TiltLimits(PitchForward: 15, PitchBackward: 55, Roll: 20));
        Assert.Equal(13, bytes.Length);
        Assert.Equal(55, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1)));
        Assert.Equal(15, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(5)));
        Assert.Equal(20, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(9)));
    }

    [Fact]
    public void Power_is_clamped_to_0_99()
    {
        Assert.Equal(99, BinaryPrimitives.ReadInt32BigEndian(Packets.SetPower(150).AsSpan(1)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(Packets.SetPower(-5).AsSpan(1)));
    }

    [Fact]
    public void Parameter_dump_matches_GameLink_offsets()
    {
        var p = new DeviceParameters
        {
            Power = 77, TiltLimits = new(15, 55, 20), YawLimit = 260, HasYawLimit = true,
            YawReturnLimit = 12.5f, HasVibration = true, VibrationLimit = 9, LedConfig = Enumerable.Range(1, 37).Select(i => (byte)i).ToArray(),
        };
        var b = Packets.ParameterDump(p, YawModel.Yaw2);

        // The exact reads in YawController.TCPMessageReceived, case 50.
        Assert.Equal(CommandIds.SetPower, b[0]);
        Assert.Equal(77, b[4]);
        Assert.Equal(55, b[9]);   // PitchLimitB
        Assert.Equal(15, b[13]);  // PitchLimitF
        Assert.Equal(20, b[17]);  // RollLimit
        Assert.Equal(260, BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(19)));
        Assert.Equal(1, b[24]);   // HasYawLimit
        Assert.Equal(12.5f, BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(26)));
        Assert.Equal(1, b[31]);   // HasVibration
        Assert.Equal(9, b[32]);   // VibrationLimit
        Assert.Equal(1, b[34]);   // first LED config byte
        Assert.Equal(37, b[70]);  // last LED config byte GameLink copies
    }

    [Fact]
    public void Check_in_answer_gives_GameLink_three_fields_with_edition()
    {
        var info = new DeviceInfo(YawModel.Yaw2, "CHAIR01", "Chair") { Edition = DeviceEdition.Pro };
        var bytes = Packets.CheckInAnswer(CheckInResponse.Accept(), info);
        Assert.Equal(CommandIds.CheckInAnswer, bytes[0]);
        var fields = Encoding.ASCII.GetString(bytes, 1, bytes.Length - 1).Split(';');
        Assert.True(fields.Length >= 3);
        Assert.True(Enum.TryParse<DeviceEdition>(fields[2], true, out var edition));
        Assert.Equal(DeviceEdition.Pro, edition);
    }

    [Fact]
    public void App_check_in_answer_has_a_total_length_byte()
    {
        var bytes = Packets.CheckInAppAnswer(false, false, "", YawModel.Yaw2);
        Assert.Equal(CommandIds.CheckInAppAnswer, bytes[0]);
        Assert.Equal(bytes.Length, bytes[1]);
        Assert.Equal("AVAILABLE;200;0;disabled;;YAWDEVICE2", Encoding.ASCII.GetString(bytes, 2, bytes.Length - 2));
    }

    [Theory]
    [InlineData(0f, "000.000")]
    [InlineData(180f, "180.000")]
    [InlineData(270f, "-090.000")]
    [InlineData(-12.345f, "-012.345")]
    public void Angles_format_like_both_sources(float value, string expected) =>
        Assert.Equal(expected, TextFormats.Angle(value));

    [Fact]
    public void Motion_round_trips_with_suffixes()
    {
        var text = TextFormats.Motion(new Orientation(12.345f, -1.25f, 0), new Vibration(10, 40), 3);
        Assert.Equal("Y[012.345]P[-001.250]R[000.000]V[10,10,10,40]F[3,3]", text);

        var frame = TextFormats.ParseMotion(text, Anywhere)!;
        Assert.Equal(12.345f, frame.Pose.Yaw, precision: 3);
        Assert.Equal(-1.25f, frame.Pose.Pitch, precision: 3);
        Assert.Equal(new Vibration(10, 10, 10, 40), frame.Vibration);
        Assert.Equal(new[] { 3, 3 }, frame.Fan);
    }

    [Fact]
    public void SimTools_bit_range_centre_is_zero_degrees()
    {
        var frame = TextFormats.ParseMotion("YS[511]P[511]R[511]B[10]", Anywhere)!;
        Assert.Equal(10, frame.BitRange);
        Assert.Equal(Orientation.Zero, frame.Pose);
    }

    [Fact]
    public void Vibration_only_datagram_has_no_pose()
    {
        var frame = TextFormats.ParseMotion("V[5,5,5,30]", Anywhere)!;
        Assert.False(frame.HasPose);
        Assert.Equal(new Vibration(5, 30), frame.Vibration);
    }

    [Fact]
    public void Garbage_is_not_motion()
    {
        Assert.Null(TextFormats.ParseMotion("hello", Anywhere));
        Assert.Null(TextFormats.ParseMotion("Y[abc]P[1.0]R[2.0]", Anywhere));
    }

    [Fact]
    public void Telemetry_round_trips_with_battery_and_app_fields()
    {
        var t = TextFormats.ParseTelemetry(TextFormats.Telemetry(new Orientation(270, 5, -3), 3.7f))!;
        Assert.Equal(-90f, t.Actual.Yaw, precision: 3);
        Assert.Equal(3.7f, t.BatteryVoltage!.Value, precision: 3);

        var app = TextFormats.ParseTelemetry(TextFormats.AppTelemetry(new Orientation(1, 2, 3), new Orientation(4, 5, 6)))!;
        Assert.Equal(new Orientation(4, 5, 6), app.Game);
    }

    [Fact]
    public void Discovery_reply_round_trips()
    {
        var reply = new DiscoveryReply(YawModel.Yaw3, "EMU", "My Chair", 50020, true);
        Assert.Equal("YAWDEVICE3;EMU;My Chair;50020;RESERVED", reply.ToWire());
        Assert.Equal(reply, DiscoveryReply.Parse(reply.ToWire()));
        Assert.Null(DiscoveryReply.Parse("YAW_CALLING"));
    }

    [Fact]
    public void Emulator_example_discovery_reply_parses()
    {
        var reply = DiscoveryReply.Parse("YAWDEVICE;EMULATOR01;YAW_EMULATOR;50020;AVAILABLE")!;
        Assert.Equal(YawModel.Yaw1, reply.Model);
        Assert.Equal("YAW_EMULATOR", reply.Name);
        Assert.False(reply.Reserved);
    }
}
