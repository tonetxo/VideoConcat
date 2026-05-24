using System;
using System.Collections.Generic;
using Xunit;

namespace VideoConcat.Tests;

public class ParseDurationsTests
{
    [Fact]
    public void EmptyString_ReturnsEmpty()
    {
        int[] result = ParseDurations("");
        Assert.Empty(result);
    }

    [Fact]
    public void NullString_ReturnsEmpty()
    {
        int[] result = ParseDurations(null);
        Assert.Empty(result);
    }

    [Fact]
    public void SingleValue_ReturnsSingle()
    {
        int[] result = ParseDurations("25");
        Assert.Equal(new int[] { 25 }, result);
    }

    [Fact]
    public void CommaSeparated_ReturnsMultiple()
    {
        int[] result = ParseDurations("25,30,25");
        Assert.Equal(new int[] { 25, 30, 25 }, result);
    }

    [Fact]
    public void SpaceSeparated_ReturnsMultiple()
    {
        int[] result = ParseDurations("25 30 25");
        Assert.Equal(new int[] { 25, 30, 25 }, result);
    }

    [Fact]
    public void MixedSeparators_ReturnsMultiple()
    {
        int[] result = ParseDurations("25, 30\t25");
        Assert.Equal(new int[] { 25, 30, 25 }, result);
    }

    [Fact]
    public void IgnoresZero()
    {
        int[] result = ParseDurations("25,0,30");
        Assert.Equal(new int[] { 25, 30 }, result);
    }

    [Fact]
    public void IgnoresNegative()
    {
        int[] result = ParseDurations("25,-5,30");
        Assert.Equal(new int[] { 25, 30 }, result);
    }

    [Fact]
    public void IgnoresNonNumeric()
    {
        int[] result = ParseDurations("25,abc,30");
        Assert.Equal(new int[] { 25, 30 }, result);
    }

    [Fact]
    public void TrailingComma_IgnoresEmpty()
    {
        int[] result = ParseDurations("25,30,");
        Assert.Equal(new int[] { 25, 30 }, result);
    }

    [Fact]
    public void LeadingComma_IgnoresEmpty()
    {
        int[] result = ParseDurations(",25,30");
        Assert.Equal(new int[] { 25, 30 }, result);
    }

    [Fact]
    public void WhitespaceOnly_ReturnsEmpty()
    {
        int[] result = ParseDurations("   ");
        Assert.Empty(result);
    }

    static int[] ParseDurations(string raw)
    {
        string[] parts = (raw ?? "").Split(new char[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        List<int> durations = new List<int>();
        foreach (string part in parts)
        {
            if (int.TryParse(part.Trim(), out int duration) && duration > 0)
            {
                durations.Add(duration);
            }
        }
        return durations.ToArray();
    }
}

public class GetValidTransitionFramesTests
{
    [Theory]
    [InlineData(12, 13)]
    [InlineData(8, 9)]
    [InlineData(16, 17)]
    [InlineData(5, 5)]
    [InlineData(9, 9)]
    [InlineData(13, 13)]
    [InlineData(4, 5)]
    [InlineData(20, 21)]
    public void Wan21_AdjustsTo4nPlus1(int input, int expected)
    {
        int result = GetValidTransitionFrames(input, "wan-21");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(12, 13)]
    [InlineData(8, 9)]
    public void Wan22_AdjustsTo4nPlus1(int input, int expected)
    {
        int result = GetValidTransitionFrames(input, "wan-22");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(1)]
    [InlineData(60)]
    public void NonWanModels_NoAdjustment(int input)
    {
        int result = GetValidTransitionFrames(input, "ltx-video");
        Assert.Equal(input, result);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(8)]
    public void EmptyCompat_NoAdjustment(int input)
    {
        int result = GetValidTransitionFrames(input, "");
        Assert.Equal(input, result);
    }

    static int GetValidTransitionFrames(int frames, string compatClass)
    {
        if (compatClass.StartsWith("wan-21") || compatClass.StartsWith("wan-22"))
        {
            int remainder = (frames - 1) % 4;
            if (remainder == 0)
            {
                return frames;
            }
            int lower = frames - remainder;
            int upper = lower + 4;
            int validFrames = (frames - lower) < (upper - frames) ? lower : upper;
            validFrames = Math.Max(5, validFrames);
            return validFrames;
        }

        return frames;
    }
}