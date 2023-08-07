using System.Net;
using GeeksCoreLibrary.Components.OrderProcess.Models;
using GeeksCoreLibrary.Components.ShoppingBasket;
using GeeksCoreLibrary.Components.ShoppingBasket.Interfaces;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Enums;
using GeeksCoreLibrary.Core.Extensions;
using GeeksCoreLibrary.Core.Helpers;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using GeeksCoreLibrary.Modules.Payments.Enums;
using GeeksCoreLibrary.Modules.Payments.Interfaces;
using GeeksCoreLibrary.Modules.Payments.Models;
using GeeksCoreLibrary.Modules.Payments.RaboSmartPay.Models;
using GeeksCoreLibrary.Modules.Payments.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using OmniKassa.Exceptions;
using OmniKassa.Model;
using OmniKassa.Model.Enums;
using OmniKassa.Model.Order;
using OmniKassa.Model.Response;
using OmniKassa.Model.Response.Notification;
using RaboSmartPayConstants = GeeksCoreLibrary.Modules.Payments.RaboSmartPay.Models.Constants;
using Constants = GeeksCoreLibrary.Components.OrderProcess.Models.Constants;

namespace GeeksCoreLibrary.Modules.Payments.RaboSmartPay.Services;

/// <inheritdoc cref="IPaymentServiceProviderService" />
public class RaboSmartPayService : PaymentServiceProviderBaseService, IPaymentServiceProviderService, ITransientService
{
    private readonly IShoppingBasketsService shoppingBasketsService;
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly IDatabaseConnection databaseConnection;
    private readonly GclSettings gclSettings;

    private OmniKassa.Environment environment = OmniKassa.Environment.SANDBOX;
    private string refreshToken = "";
    private string signingKey = "";

    public RaboSmartPayService(IShoppingBasketsService shoppingBasketsService,
                               IDatabaseConnection databaseConnection,
                               IOptions<GclSettings> gclSettings,
                               IDatabaseHelpersService databaseHelpersService,
                               ILogger<RaboSmartPayService> logger,
                               IHttpContextAccessor httpContextAccessor = null)
        : base(databaseHelpersService, databaseConnection, logger, httpContextAccessor)
    {
        this.shoppingBasketsService = shoppingBasketsService;
        this.httpContextAccessor = httpContextAccessor;
        this.databaseConnection = databaseConnection;
        this.gclSettings = gclSettings.Value;
    }

    /// <summary>
    /// Set the refresh token, signing key and environment based on the environment.
    /// </summary>
    /// <returns></returns>
    private void SetupEnvironment(RaboSmartPaySettingsModel settings)
    {
        refreshToken = settings.RefreshToken;
        signingKey = settings.SigningKey;
        environment = gclSettings.Environment.InList(Environments.Acceptance, Environments.Live) ? OmniKassa.Environment.PRODUCTION : OmniKassa.Environment.SANDBOX;
    }

    /// <inheritdoc />
    public async Task<PaymentRequestResult> HandlePaymentRequestAsync(ICollection<(WiserItemModel Main, List<WiserItemModel> Lines)> shoppingBaskets, WiserItemModel userDetails, PaymentMethodSettingsModel paymentMethodSettings, string invoiceNumber)
    {
        var basketSettings = await shoppingBasketsService.GetSettingsAsync();
        var raboSmartPaySettings = (RaboSmartPaySettingsModel) paymentMethodSettings.PaymentServiceProvider;

        var totalPrice = 0M;
        foreach (var (main, lines) in shoppingBaskets)
        {
            totalPrice += await shoppingBasketsService.GetPriceAsync(main, lines, basketSettings, ShoppingBasket.PriceTypes.PspPriceInVat);
        }

        var orderBuilder = new MerchantOrder.Builder()
            .WithMerchantOrderId(invoiceNumber)
            .WithAmount(Money.FromDecimal(Currency.EUR, totalPrice))
            .WithMerchantReturnURL(paymentMethodSettings.PaymentServiceProvider.SuccessUrl)
            .WithOrderItems(CreateOrderItems(shoppingBaskets));

        try
        {
            var billingAddress = CreateAddress(userDetails);
            var shippingDetails = CreateAddress(userDetails, "shipping_") ?? billingAddress; //If no shipping address has been provided use billing address.
            var paymentBrand = ConvertPaymentMethodToPaymentBrand(paymentMethodSettings);

            orderBuilder.WithBillingDetail(billingAddress)
                .WithShippingDetail(shippingDetails)
                .WithPaymentBrand(paymentBrand);
        }

        //Converting the country code throws an argument exception if the code is not supported.
        //Converting payment method throws an argument exception if the method is not supported.
        catch (ArgumentException)
        {
            return new PaymentRequestResult
            {
                Action = PaymentRequestActions.Redirect,
                ActionData = paymentMethodSettings.PaymentServiceProvider.FailUrl,
                Successful = false,
                ErrorMessage = $"Unknown or unsupported payment method '{paymentMethodSettings:G}'"
            };
        }

        orderBuilder.WithPaymentBrandForce(PaymentBrandForce.FORCE_ALWAYS); //Don't allow customers to change payment method on the Rabo OmniKassa website.

        var merchantOrder = orderBuilder.Build();

        SetupEnvironment(raboSmartPaySettings);

        var endpoint = OmniKassa.Endpoint.Create(environment, signingKey, refreshToken);

        MerchantOrderResponse response;

        try
        {
            response = await endpoint.Announce(merchantOrder);
        }
        catch (InvalidAccessTokenException)
        {
            return new PaymentRequestResult
            {
                Action = PaymentRequestActions.Redirect,
                ActionData = paymentMethodSettings.PaymentServiceProvider.FailUrl,
                Successful = false,
                ErrorMessage = "Failed to authenticate with Rabo omni kassa API"
            };
        }

        return new PaymentRequestResult()
        {
            Successful = true,
            Action = PaymentRequestActions.Redirect,
            ActionData = response.RedirectUrl
        };
    }

    /// <summary>
    /// Convert the order lines in the shopping baskets to a <see cref="OrderItem"/>.
    /// </summary>
    /// <param name="shoppingBaskets">The shopping baskets to convert.</param>
    /// <returns>A collection of <see cref="OrderItem"/>s.</returns>
    private List<OrderItem> CreateOrderItems(ICollection<(WiserItemModel Main, List<WiserItemModel> Lines)> shoppingBaskets)
    {
        var orderItems = new List<OrderItem>();

        foreach (var (main, lines) in shoppingBaskets)
        {
            foreach (var line in lines)
            {
                // Get the title of the product. If it is a coupon no title is provided and description will be used instead.
                var name = line.GetDetailValue("title");
                if (String.IsNullOrWhiteSpace(name))
                {
                    name = line.GetDetailValue("description");
                }

                var orderItem = new OrderItem.Builder()
                    .WithId(line.GetDetailValue(Components.ShoppingBasket.Models.Constants.ConnectedItemIdProperty))
                    .WithName(name)
                    .WithDescription(name)
                    .WithQuantity(line.GetDetailValue<int>("quantity"))
                    .WithAmount(Money.FromDecimal(Currency.EUR, line.GetDetailValue<decimal>("price")))
                    .Build();

                orderItems.Add(orderItem);
            }
        }

        return orderItems;
    }

    /// <summary>
    /// Convert the user details to an <see cref="Address"/>.
    /// Throws <see cref="ArgumentException"/> when the provided country code does not correspond with a country supported by Rabo OmniKassa.
    /// </summary>
    /// <param name="userDetails">The <see cref="WiserItemModel"/> containing the user details.</param>
    /// <param name="detailKeyPrefix">Additional string as a prefix for "street", "zipcode", "city", "country", "housenumber" and "housenumber_suffix". For example for shipping.</param>
    /// <returns>Returns an <see cref="Address"/> with the required information.</returns>
    private Address CreateAddress(WiserItemModel userDetails, string detailKeyPrefix = "")
    {
        //If a prefix is given but any of the required values doesn't contain a value return null.
        if (!String.IsNullOrWhiteSpace(detailKeyPrefix) &&
            (String.IsNullOrWhiteSpace(userDetails.GetDetailValue($"{detailKeyPrefix}street")))
            || String.IsNullOrWhiteSpace(userDetails.GetDetailValue($"{detailKeyPrefix}zipcode"))
            || String.IsNullOrWhiteSpace(userDetails.GetDetailValue($"{detailKeyPrefix}city"))
            || String.IsNullOrWhiteSpace(userDetails.GetDetailValue($"{detailKeyPrefix}country")))
        {
            return null;
        }

        var addressBuilder = new Address.Builder()
            .WithFirstName(userDetails.GetDetailValue("firstname"))
            .WithLastName(userDetails.GetDetailValue("lastname"))
            .WithStreet(userDetails.GetDetailValue($"{detailKeyPrefix}street"))
            .WithPostalCode(userDetails.GetDetailValue($"{detailKeyPrefix}zipcode"))
            .WithCity(userDetails.GetDetailValue($"{detailKeyPrefix}city"))
            .WithCountryCode(EnumHelpers.ToEnum<CountryCode>(userDetails.GetDetailValue($"{detailKeyPrefix}country")));

        //Add house number if provided to Wiser.
        var houseNumber = userDetails.GetDetailValue($"{detailKeyPrefix}housenumber");
        if (String.IsNullOrWhiteSpace(houseNumber))
        {
            return addressBuilder.Build();
        }

        addressBuilder.WithHouseNumber(houseNumber);

        //Add house number addition if an addition has been provided to Wiser.
        var houseNumberAddition = userDetails.GetDetailValue($"{detailKeyPrefix}housenumber_suffix");
        if (!String.IsNullOrWhiteSpace(houseNumberAddition))
        {
            addressBuilder.WithHouseNumberAddition(houseNumberAddition);
        }

        return addressBuilder.Build();
    }

    /// <summary>
    /// Convert <see cref="PaymentMethods"/> to <see cref="PaymentBrand"/>.
    /// Throws <see cref="ArgumentException"/> when the provided <see cref="PaymentMethods"/> is not supported by Rabo OmniKassa.
    /// </summary>
    /// <param name="paymentMethod">The <see cref="PaymentMethods"/> to convert.</param>
    /// <returns>Returns the <see cref="PaymentBrand"/> of the corresponding brand.</returns>
    private PaymentBrand ConvertPaymentMethodToPaymentBrand(PaymentMethodSettingsModel paymentMethod)
    {
        switch (paymentMethod.ExternalName.ToUpperInvariant())
        {
            case "IDEAL":
                return PaymentBrand.IDEAL;
            case "AFTERPAY":
                return PaymentBrand.AFTERPAY;
            case "PAYPAL":
                return PaymentBrand.PAYPAL;
            case "MASTERCARD":
                return PaymentBrand.MASTERCARD;
            case "VISA":
                return PaymentBrand.VISA;
            case "BANCONTACT":
                return PaymentBrand.BANCONTACT;
            case "MAESTRO":
                return PaymentBrand.MAESTRO;
            case "V_PAY":
            case "VPAY":
                return PaymentBrand.V_PAY;
            default:
                throw new ArgumentOutOfRangeException(nameof(paymentMethod.ExternalName), paymentMethod.ExternalName);
        }
    }

    /// <inheritdoc />
    public async Task<StatusUpdateResult> ProcessStatusUpdateAsync(OrderProcessSettingsModel orderProcessSettings, PaymentMethodSettingsModel paymentMethodSettings)
    {
        if (httpContextAccessor?.HttpContext == null)
        {
            return new StatusUpdateResult
            {
                Status = "Request not available; unable to process status update.",
                Successful = false
            };
        }

        var raboSmartPaySettings = (RaboSmartPaySettingsModel) paymentMethodSettings.PaymentServiceProvider;
        SetupEnvironment(raboSmartPaySettings);

        PaymentCompletedResponse response;
        try
        {
            response = CreatePaymentCompletedResponse();
        }
        catch (IllegalSignatureException)
        {
            return new StatusUpdateResult
            {
                Status = "Illegal signature received; unable to process status update.",
                Successful = false
            };
        }

        await LogIncomingPaymentActionAsync(PaymentServiceProviders.RaboSmartPay, response.OrderId, Convert.ToInt32(response.Status));

        switch (response.Status)
        {
            case PaymentStatus.COMPLETED:
                return new StatusUpdateResult
                {
                    Successful = true
                };
            case PaymentStatus.CANCELLED:
                return new StatusUpdateResult
                {
                    Status = "User cancelled the order at the PSP.",
                    Successful = false
                };
            case PaymentStatus.EXPIRED:
                return new StatusUpdateResult
                {
                    Status = "The order expired at the PSP.",
                    Successful = false
                };
            default:
                return new StatusUpdateResult
                {
                    Status = "Unknown status; unable to process status update.",
                    Successful = false
                };
        }
    }

    /// <inheritdoc />
    public async Task<PaymentServiceProviderSettingsModel> GetProviderSettingsAsync(PaymentServiceProviderSettingsModel paymentServiceProviderSettings)
    {
        databaseConnection.AddParameter("id", paymentServiceProviderSettings.Id);

        var query = $@"SELECT
    raboSmartPayRefreshTokenLive.`value` AS raboSmartPayRefreshTokenLive,
    raboSmartPayRefreshTokenTest.`value` AS raboSmartPayRefreshTokenTest,
    raboSmartPaySigningKeyLive.`value` AS raboSmartPaySigningKeyLive,
    raboSmartPaySigningKeyTest.`value` AS raboSmartPaySigningKeyTest
FROM {WiserTableNames.WiserItem} AS paymentServiceProvider
LEFT JOIN {WiserTableNames.WiserItemDetail} AS raboSmartPayRefreshTokenLive ON raboSmartPayRefreshTokenLive.item_id = paymentServiceProvider.id AND raboSmartPayRefreshTokenLive.`key` = '{RaboSmartPayConstants.RaboSmartPayRefreshTokenLiveProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS raboSmartPayRefreshTokenTest ON raboSmartPayRefreshTokenTest.item_id = paymentServiceProvider.id AND raboSmartPayRefreshTokenTest.`key` = '{RaboSmartPayConstants.RaboSmartPayRefreshTokenTestProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS raboSmartPaySigningKeyLive ON raboSmartPaySigningKeyLive.item_id = paymentServiceProvider.id AND raboSmartPaySigningKeyLive.`key` = '{RaboSmartPayConstants.RaboSmartPaySigningKeyLiveProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS raboSmartPaySigningKeyTest ON raboSmartPaySigningKeyTest.item_id = paymentServiceProvider.id AND raboSmartPaySigningKeyTest.`key` = '{RaboSmartPayConstants.RaboSmartPaySigningKeyTestProperty}'
WHERE paymentServiceProvider.id = ?id
AND paymentServiceProvider.entity_type = '{Constants.PaymentServiceProviderEntityType}'";


        var result = new RaboSmartPaySettingsModel
        {
            Id = paymentServiceProviderSettings.Id,
            Title = paymentServiceProviderSettings.Title,
            Type = paymentServiceProviderSettings.Type,
            LogAllRequests = paymentServiceProviderSettings.LogAllRequests,
            OrdersCanBeSetDirectlyToFinished = paymentServiceProviderSettings.OrdersCanBeSetDirectlyToFinished,
            SkipPaymentWhenOrderAmountEqualsZero = paymentServiceProviderSettings.SkipPaymentWhenOrderAmountEqualsZero
        };

        var dataTable = await databaseConnection.GetAsync(query);
        if (dataTable.Rows.Count == 0)
        {
            return result;
        }

        var row = dataTable.Rows[0];

        var suffix = gclSettings.Environment.InList(Environments.Development, Environments.Test) ? "Test" : "Live";
        result.RefreshToken = row.GetAndDecryptSecretKey($"raboSmartPayRefreshToken{suffix}");
        result.SigningKey = row.GetAndDecryptSecretKey($"raboSmartPaySigningKey{suffix}");
        return result;
    }

    /// <inheritdoc />
    public string GetInvoiceNumberFromRequest()
    {
        return HttpContextHelpers.GetRequestValue(httpContextAccessor?.HttpContext, RaboSmartPayConstants.WebhookInvoiceNumberProperty);
    }

    /// <summary>
    /// Create a <see cref="PaymentCompletedResponse"/> based on the request query.
    /// Throws <see cref="IllegalSignatureException"/> when the provided information by the query is not signed by the correct key.
    /// </summary>
    /// <returns>Returns a valid <see cref="PaymentCompletedResponse"/> object.</returns>
    private PaymentCompletedResponse CreatePaymentCompletedResponse()
    {
        if (httpContextAccessor?.HttpContext == null)
        {
            throw new Exception("No httpContext found. Did you add the dependency in Program.cs or Startup.cs?");
        }

        var orderId = httpContextAccessor.HttpContext.Request.Query["order_id"].ToString(); //Invoice number as provided by us.
        var status = httpContextAccessor.HttpContext.Request.Query["status"].ToString();
        var signature = httpContextAccessor.HttpContext.Request.Query["signature"].ToString();

        return PaymentCompletedResponse.Create(orderId, status, signature, signingKey);
    }

    /// <summary>
    /// Get the corresponding redirect URL after the return from the PSP website.
    /// Rabo OmniKassa only provides one return url containing information about the status of the order.
    /// </summary>
    /// <returns>Returns the url to redirect the user to.</returns>
    public string GetRedirectUrlOnReturnFromPSP(PaymentMethodSettingsModel paymentMethodSettings)
    {
        var raboSmartPaySettings = (RaboSmartPaySettingsModel) paymentMethodSettings.PaymentServiceProvider;
        if (httpContextAccessor?.HttpContext == null)
        {
            return raboSmartPaySettings.FailUrl;
        }

        SetupEnvironment(raboSmartPaySettings);

        PaymentCompletedResponse response;
        try
        {
            response = CreatePaymentCompletedResponse();
        }
        catch (IllegalSignatureException)
        {
            return raboSmartPaySettings.FailUrl;
        }

        switch (response.Status)
        {
            case PaymentStatus.COMPLETED:
                return raboSmartPaySettings.SuccessUrl;
            case PaymentStatus.IN_PROGRESS:
                var pendingUrl = raboSmartPaySettings.PendingUrl;

                //Redirect to success url if no specific pending url has been provided.
                if (String.IsNullOrWhiteSpace(pendingUrl))
                {
                    pendingUrl = raboSmartPaySettings.SuccessUrl;
                }

                return pendingUrl;
            case PaymentStatus.CANCELLED:
                return raboSmartPaySettings.FailUrl;
            case PaymentStatus.EXPIRED:
                return raboSmartPaySettings.FailUrl;
            default:
                return raboSmartPaySettings.FailUrl;
        }
    }

    /// <summary>
    /// Handle the notifications that are provided by Rabo OmniKassa by means of a webhook.
    /// </summary>
    /// <returns></returns>
    public async Task HandleNotification(PaymentMethodSettingsModel paymentMethodSettings)
    {
        if (httpContextAccessor?.HttpContext == null)
        {
            return;
        }

        var raboSmartPaySettings = (RaboSmartPaySettingsModel) paymentMethodSettings.PaymentServiceProvider;
        SetupEnvironment(raboSmartPaySettings);

        //Get the notification from the body.
        using var reader = new StreamReader(httpContextAccessor.HttpContext.Request.Body);
        var bodyJson = await reader.ReadToEndAsync();
        var notification = JsonConvert.DeserializeObject<ApiNotification>(bodyJson);

        if (notification == null)
        {
            return;
        }

        try
        {
            notification.ValidateSignature(signingKey);
        }
        catch (IllegalSignatureException)
        {
            return;
        }

        var notifyUrlBase = raboSmartPaySettings.WebhookUrl;
        var endpoint = OmniKassa.Endpoint.Create(environment, signingKey, refreshToken);

        //Retrieve all MerchantOrderStatusResponses that are available.
        MerchantOrderStatusResponse response;
        do
        {
            response = await endpoint.RetrieveAnnouncement(notification);
            try
            {
                response.ValidateSignature(signingKey);
            }
            catch (IllegalSignatureException)
            {
                return;
            }

            //Handle each MerchantOrderResult separately to comply with the operation of the PaymentService.
            foreach (var result in response.OrderResults)
            {
                //Ignore updates with the status of "IN_PROGRESS" in case those are given, only handle definitive states.
                if (result.OrderStatus == PaymentStatus.IN_PROGRESS)
                {
                    await LogIncomingPaymentActionAsync(PaymentServiceProviders.RaboSmartPay, result.MerchantOrderId, Convert.ToInt32(result.OrderStatus));
                    continue;
                }

                //Prepare the signature data. The information and order needs to be the same as in PaymentCompletedResponse.
                var signatureData = new List<string>
                {
                    result.MerchantOrderId,
                    result.OrderStatus.ToString()
                };
                var signature = Signable.CalculateSignature(signatureData, Convert.FromBase64String(signingKey));

                var notifyUrl = $"{notifyUrlBase}&order_id={result.MerchantOrderId}&status={result.OrderStatus}&signature={signature}";

                var request = (HttpWebRequest) WebRequest.Create(notifyUrl);
                _ = request.GetResponseAsync(); //Ignore the return value.
            }
        } while (response.MoreOrderResultsAvailable);
    }
}