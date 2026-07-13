using DrumPracticeStudio.Midi;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class DeviceAutoConfigurationTests
{
    [TestMethod]
    public void Audio_SavedDeviceAlwaysWins()
    {
        AudioOutputDeviceItem[] devices =
        [
            new("wasapi-focusrite", "Altavoces (Focusrite USB Audio)", true),
            new("asio-focusrite", "Focusrite USB ASIO", false, AudioOutputBackend.Asio)
        ];

        var selected = DeviceAutoConfiguration.SelectAudioOutput(devices, "wasapi-focusrite");

        Assert.AreEqual("wasapi-focusrite", selected?.Id);
    }

    [TestMethod]
    public void Audio_FirstRunMatchesNativeAsioToDefaultInterface()
    {
        AudioOutputDeviceItem[] devices =
        [
            new("wasapi-focusrite", "Altavoces (2- Focusrite USB Audio)", true),
            new("asio-other", "Yamaha Steinberg USB ASIO", false, AudioOutputBackend.Asio),
            new("asio-focusrite", "Focusrite USB ASIO", false, AudioOutputBackend.Asio)
        ];

        var selected = DeviceAutoConfiguration.SelectAudioOutput(devices, savedDeviceId: null);

        Assert.AreEqual("asio-focusrite", selected?.Id);
    }

    [TestMethod]
    public void Audio_GenericAsioDoesNotReplaceDefaultWasapiAutomatically()
    {
        AudioOutputDeviceItem[] devices =
        [
            new("wasapi-realtek", "Altavoces (Realtek High Definition Audio)", true),
            new("asio4all", "ASIO4ALL v2", false, AudioOutputBackend.Asio)
        ];

        var selected = DeviceAutoConfiguration.SelectAudioOutput(devices, savedDeviceId: null);

        Assert.AreEqual("wasapi-realtek", selected?.Id);
    }

    [TestMethod]
    public void Midi_SavedNameSurvivesAChangedWindowsDeviceIndex()
    {
        MidiDeviceItem[] devices =
        [
            new(0, "Microsoft MIDI"),
            new(1, "MPK mini 3")
        ];

        var selected = DeviceAutoConfiguration.SelectMidiInput(devices, "MPK mini 3", savedIndex: 4);

        Assert.AreEqual(1, selected?.Index);
    }

    [TestMethod]
    public void Midi_FirstRunPrefersElectronicDrumOrPadController()
    {
        MidiDeviceItem[] devices =
        [
            new(0, "Microsoft MIDI"),
            new(1, "MPK mini 3")
        ];

        var selected = DeviceAutoConfiguration.SelectMidiInput(devices, savedName: null, savedIndex: null);

        Assert.AreEqual("MPK mini 3", selected?.Name);
    }
}
