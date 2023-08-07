using GeeksCoreLibrary.Components.OrderProcess.Models;

namespace GeeksCoreLibrary.Modules.Payments.RaboSmartPay.Models;

public class RaboSmartPaySettingsModel : PaymentServiceProviderSettingsModel
{
    /// <summary>
    /// Gets or sets the refresh token for the current environment.
    /// </summary>
    public string RefreshToken { get; set; }

    /// <summary>
    /// Gets or sets the signing key for the current environment.
    /// </summary>
    public string SigningKey { get; set; }
}