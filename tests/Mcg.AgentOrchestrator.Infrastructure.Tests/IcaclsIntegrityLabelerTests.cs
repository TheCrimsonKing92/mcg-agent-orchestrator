using System.Buffers.Binary;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class IcaclsIntegrityLabelerTests
{
    [Xunit.Fact]
    public void DecodeMandatoryLabelAcl_LowObjectAndContainerInheritableNoWriteUp_IsTrusted()
    {
        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(
            BuildAcl(BuildMandatoryLabelAce(0x01 | 0x02, 0x00000001, 0x00001000)));

        Assert.True(state.Exists);
        Assert.True(state.Low);
        Assert.True(state.Inheritable);
        Assert.False(state.Medium);
    }

    [Xunit.Fact]
    public void DecodeMandatoryLabelAcl_MediumNoWriteUp_IsMediumNotLow()
    {
        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(
            BuildAcl(BuildMandatoryLabelAce(0, 0x00000001, 0x00002000)));

        Assert.True(state.Exists);
        Assert.True(state.Medium);
        Assert.False(state.Low);
        Assert.False(state.Inheritable);
    }

    [Xunit.Fact]
    public void DecodeMandatoryLabelAcl_InheritOnlyLabel_FailsClosed()
    {
        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(
            BuildAcl(BuildMandatoryLabelAce(0x01 | 0x02 | 0x08, 0x00000001, 0x00001000)));

        Assert.True(state.Exists);
        Assert.False(state.Low);
        Assert.False(state.Medium);
        Assert.False(state.Inheritable);
    }

    [Xunit.Fact]
    public void DecodeMandatoryLabelAcl_MissingNoWriteUpPolicy_FailsClosed()
    {
        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(
            BuildAcl(BuildMandatoryLabelAce(0x01 | 0x02, 0, 0x00001000)));

        Assert.False(state.Low);
        Assert.False(state.Medium);
        Assert.False(state.Inheritable);
    }

    [Xunit.Fact]
    public void DecodeMandatoryLabelAcl_MultipleMandatoryLabels_FailsClosed()
    {
        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(BuildAcl(
            BuildMandatoryLabelAce(0x01 | 0x02, 0x00000001, 0x00001000),
            BuildMandatoryLabelAce(0x01 | 0x02, 0x00000001, 0x00002000)));

        Assert.False(state.Low);
        Assert.False(state.Medium);
        Assert.False(state.Inheritable);
    }

    [Xunit.Fact]
    public void DecodeMandatoryLabelAcl_InvalidSidAuthority_FailsClosed()
    {
        var invalidAuthority = BuildMandatoryLabelAce(0x01 | 0x02, 0x00000001, 0x00001000);
        invalidAuthority[10] = 15;

        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(BuildAcl(invalidAuthority));

        Assert.False(state.Low);
        Assert.False(state.Medium);
        Assert.False(state.Inheritable);
    }

    [Xunit.Fact]
    public void DecodeMandatoryLabelAcl_InvalidAclBounds_FailsClosed()
    {
        var malformedAcl = BuildAcl(BuildMandatoryLabelAce(0x01 | 0x02, 0x00000001, 0x00001000));
        malformedAcl[2]++;

        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(malformedAcl);

        Assert.False(state.Low);
        Assert.False(state.Medium);
        Assert.False(state.Inheritable);
    }

    [Xunit.Fact]
    public void DecodeMandatoryLabelAcl_UnsupportedAclRevision_FailsClosed()
    {
        var acl = BuildAcl(BuildMandatoryLabelAce(0x01 | 0x02, 0x00000001, 0x00001000));
        acl[0] = 3;

        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(acl);

        Assert.False(state.Low);
        Assert.False(state.Medium);
        Assert.False(state.Inheritable);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0x01)]
    [Xunit.InlineData(0x02)]
    public void DecodeMandatoryLabelAcl_PartialInheritance_IsNotTrusted(byte inheritanceFlag)
    {
        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(
            BuildAcl(BuildMandatoryLabelAce(inheritanceFlag, 0x00000001, 0x00001000)));

        Assert.True(state.Exists);
        Assert.True(state.Low);
        Assert.False(state.Medium);
        Assert.False(state.Inheritable);
    }

    [Xunit.Fact]
    public void DecodeMandatoryLabelAcl_WithoutMandatoryLabel_FailsClosed()
    {
        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(BuildAcl(BuildNonLabelAce()));

        Assert.True(state.Exists);
        Assert.False(state.Low);
        Assert.False(state.Medium);
        Assert.False(state.Inheritable);
    }

    [Xunit.Fact]
    public void DecodeMandatoryLabelAcl_InvalidSidLength_FailsClosed()
    {
        var malformedAce = BuildMandatoryLabelAce(0x01 | 0x02, 0x00000001, 0x00001000);
        Array.Resize(ref malformedAce, malformedAce.Length + 4);
        BinaryPrimitives.WriteUInt16LittleEndian(malformedAce.AsSpan(2), checked((ushort)malformedAce.Length));

        var state = IcaclsIntegrityLabeler.DecodeMandatoryLabelAcl(BuildAcl(malformedAce));

        Assert.False(state.Low);
        Assert.False(state.Medium);
        Assert.False(state.Inheritable);
    }

    [Xunit.Fact]
    public void Query_UnsupportedPlatform_FailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var state = new IcaclsIntegrityLabeler().Query(Path.GetTempPath());

        Assert.False(state.Exists);
        Assert.False(state.Low);
        Assert.False(state.Medium);
        Assert.False(state.Inheritable);
    }

    [Xunit.Fact]
    public void Query_MissingPathFailsClosedWithoutStartingTheSetIntegrityHelper()
    {
        var processStarted = false;
        var labeler = new IcaclsIntegrityLabeler(_ =>
        {
            processStarted = true;
            return null;
        });

        var state = labeler.Query(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n")));

        Assert.False(processStarted);
        Assert.False(state.Exists);
        Assert.False(state.Low);
        Assert.False(state.Medium);
        Assert.False(state.Inheritable);
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(state.NativeQueryError);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Query_ProductionWrittenInheritableLowLabel_RoundTripsAsTrusted(bool longPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-native-label-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = root;
            if (longPath)
            {
                while (target.Length <= 300)
                {
                    target = Path.Combine(target, new string('p', 50));
                }
                Directory.CreateDirectory(target);
            }

            var labeler = new IcaclsIntegrityLabeler();

            Assert.True(labeler.SetIntegrity(target, "(OI)(CI)L", recursive: false));
            var low = labeler.Query(target);
            Assert.True(low.Exists);
            Assert.True(low.Low);
            Assert.True(low.Inheritable);
            Assert.False(low.Medium);
            Assert.Null(low.NativeQueryError);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public void NativeFilePath_PreservesObjectIdentityAcrossWindowsPathForms()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var suffix = string.Join("\\", Enumerable.Repeat(new string('p', 50), 6));
        var relative = Path.Combine("parent", "..", suffix);
        Assert.Equal(@"\\?\" + Path.GetFullPath(relative), IcaclsIntegrityLabeler.NativeFilePath(relative));
        var unc = @"\\server\share\" + suffix;
        Assert.Equal(@"\\?\UNC\server\share\" + suffix, IcaclsIntegrityLabeler.NativeFilePath(unc));
        var extended = @"\\?\C:\" + suffix;
        Assert.Equal(extended, IcaclsIntegrityLabeler.NativeFilePath(extended));
        Assert.Equal(@"C:\short", IcaclsIntegrityLabeler.NativeFilePath(@"C:\short"));
    }

    private static byte[] BuildAcl(params byte[][] aces)
    {
        var length = 8 + aces.Sum(ace => ace.Length);
        var acl = new byte[length];
        acl[0] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(2), checked((ushort)length));
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(4), checked((ushort)aces.Length));
        var offset = 8;
        foreach (var ace in aces)
        {
            ace.CopyTo(acl, offset);
            offset += ace.Length;
        }

        return acl;
    }

    private static byte[] BuildMandatoryLabelAce(byte flags, uint mask, uint rid)
    {
        var ace = new byte[20];
        ace[0] = 0x11;
        ace[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(ace.AsSpan(2), checked((ushort)ace.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(ace.AsSpan(4), mask);
        ace[8] = 1;
        ace[9] = 1;
        ace[15] = 16;
        BinaryPrimitives.WriteUInt32LittleEndian(ace.AsSpan(16), rid);
        return ace;
    }

    private static byte[] BuildNonLabelAce()
    {
        var ace = new byte[4];
        ace[0] = 0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(ace.AsSpan(2), checked((ushort)ace.Length));
        return ace;
    }
}
