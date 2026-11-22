// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ScenarioVersionBoundaryTests
{
    [Theory]
    [InlineData("night", "Night mode")]
    [InlineData("layout", "invalid layout")]
    [InlineData("missing-views", "missing views")]
    [InlineData("script-aim", "script aim target")]
    [InlineData("navigation-version", "Camera poses")]
    [InlineData("navigation-range", "invalid camera pose")]
    [InlineData("field", "field or observer frame")]
    [InlineData("rate", "playback rate")]
    [InlineData("marks", "reference marks")]
    [InlineData("lighttime", "Light-time correction")]
    public void InvalidDocumentVersionsAndCameraStateAreRejectedBeforeRestore(string fault,string diagnostic)
    {
        var document=JsonNode.Parse("""
            {"version":51,"views":[{"dayTdb":2451545,"nanosecondsOfDay":0,"camera":"solar","selection":null,"playing":false}],
             "synchronized":false,"clockOwner":0,"activeView":0,"deltaT":"morrisonStephenson2004"}
            """)!.AsObject();
        var view=document["views"]![0]!;
        switch(fault)
        {
            case "night": document["version"]=34; document["nightMode"]=true; break;
            case "layout": document["version"]=5; document["splitLayout"]="stacked"; break;
            case "missing-views": document["views"]=null; break;
            case "script-aim": document["version"]=48; view["scriptAimTarget"]="sol/moon"; break;
            case "navigation-version": document["version"]=1; view["navigation"]=JsonNode.Parse("{\"rangeKilometers\":1}"); break;
            case "navigation-range": view["navigation"]=JsonNode.Parse("{\"rangeKilometers\":0}"); break;
            case "field": view["fieldOfViewDegrees"]=0; break;
            case "rate": document["version"]=6; view["playbackRate"]=1; break;
            case "marks": document["version"]=8; view["referenceMarks"]="bodyAxes"; break;
            case "lighttime": document["version"]=9; view["lightTimeDelayed"]=true; break;
        }
        var error=Assert.Throws<InvalidDataException>(()=>ScenarioFile.Decode(Encoding.UTF8.GetBytes(document.ToJsonString())));
        Assert.Contains(diagnostic,error.Message,StringComparison.OrdinalIgnoreCase);
    }

    // Hand-authored wire documents: these do not depend on the current writer choosing a version.
    [Theory]
    [InlineData(4, "{\"stars\":true}", "Sky layers")]
    [InlineData(5, "{\"deepSky\":true}", "deep-sky layer")]
    [InlineData(12, "{\"meteors\":true}", "meteor layers")]
    [InlineData(15, "{\"cultureId\":\"western\"}", "culture settings")]
    [InlineData(16, "{\"surveyId\":\"local\"}", "culture settings")]
    [InlineData(24, "{\"eclipticGrid\":true}", "galactic layers")]
    [InlineData(25, "{\"constellationBoundaries\":true}", "Constellation boundaries")]
    [InlineData(26, "{\"meteorRadiants\":true}", "Independent meteor")]
    [InlineData(27, "{\"minorBodyLabels\":true}", "Minor body labels")]
    [InlineData(28, "{\"hideSatellites\":true}", "Satellite visibility")]
    [InlineData(29, "{\"hideCometTails\":true}", "Body layer preferences")]
    [InlineData(30, "{\"cinematicBlackHoles\":true}", "Cinematic black holes")]
    [InlineData(31, "{\"openClusterLabels\":true}", "Deep-sky preferences")]
    [InlineData(32, "{\"hideClouds\":true}", "Cloud visibility")]
    [InlineData(33, "{\"hideAtmospheres\":true}", "Atmosphere visibility")]
    [InlineData(34, "{\"hideTerrain\":true}", "Terrain visibility")]
    [InlineData(39, "{\"pointerCoordinates\":true}", "Pointer coordinates")]
    [InlineData(40, "{\"zodiacalLight\":true}", "Zodiacal light")]
    [InlineData(42, "{\"starlink\":false}", "Starlink layer")]
    [InlineData(43, "{\"smallBodies\":0}", "small-body swarm")]
    [InlineData(44, "{\"markings\":2}", "Sky markings require")]
    [InlineData(45, "{\"markings\":1}", "Sky markings of date")]
    [InlineData(46, "{\"markings\":4096}", "Catalogue sky markings")]
    [InlineData(47, "{\"markings\":16384}", "selected body path")]
    public void NewSkyFieldsAreAcceptedAtTheirVersionAndRejectedOneVersionEarlier(int version, string sky, string diagnostic)
    {
        var document = JsonNode.Parse("""
            {"version":1,"views":[{"dayTdb":2451545,"nanosecondsOfDay":0,"camera":"solar","selection":null,"playing":false}],
             "synchronized":false,"clockOwner":0,"activeView":0,"deltaT":"morrisonStephenson2004"}
            """)!.AsObject();
        document["version"] = version;
        document["views"]![0]!["sky"] = JsonNode.Parse(sky);
        var restored = ScenarioFile.Decode(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Assert.Single(restored.Views);
        Assert.Equal(2451545, restored.Views[0].Epoch.Day);
        document["version"] = version - 1;
        var error = Assert.Throws<InvalidDataException>(() => ScenarioFile.Decode(Encoding.UTF8.GetBytes(document.ToJsonString())));
        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
