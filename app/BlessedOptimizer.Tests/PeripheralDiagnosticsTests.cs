using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class PeripheralDiagnosticsTests
{
    [Theory]
    [InlineData("Monitor", "Generic PnP Monitor", "Monitor")]
    [InlineData("Keyboard", "USB Input Device", "Klawiatura")]
    [InlineData("Mouse", "HID-compliant mouse", "Mysz")]
    [InlineData("Camera", "Integrated Camera", "Kamera")]
    [InlineData("MEDIA", "Realtek Audio", "Dźwięk")]
    [InlineData("Image", "USB Scanner", "Obrazowanie")]
    public void ClassifiesSupportedPresentDeviceTypes(string className, string name, string expectedCategory)
    {
        Assert.Equal(expectedCategory, PeripheralDiagnostics.ClassifyDevice(className, name));
    }

    [Theory]
    [InlineData("HIDClass", "Xbox Wireless Controller", "Kontroler")]
    [InlineData("Bluetooth", "DualSense Wireless Controller", "Kontroler")]
    public void RecognizesControllersBeforeGenericPnpClasses(string className, string name, string expectedCategory)
    {
        Assert.Equal(expectedCategory, PeripheralDiagnostics.ClassifyDevice(className, name));
    }

    [Theory]
    [InlineData("USB", "USB Root Hub")]
    [InlineData("System", "ACPI x64-based PC")]
    [InlineData("", "")]
    public void IgnoresGenericDevicesOutsideTheSupportedInventory(string className, string name)
    {
        Assert.Null(PeripheralDiagnostics.ClassifyDevice(className, name));
    }
}
