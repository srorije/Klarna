﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using EPiServer;
using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Commerce.Order;
using EPiServer.Core;
using EPiServer.Globalization;
using EPiServer.ServiceLocation;
using Klarna.Payments.Models;
using Mediachase.Commerce;
using EPiServer.Logging;
using EPiServer.Web.Routing;
using Klarna.Payments.Extensions;
using Klarna.Payments.Helpers;
using Mediachase.Commerce.Catalog;
using Mediachase.Commerce.Orders;
using Mediachase.Commerce.Orders.Dto;
using Mediachase.Commerce.Orders.Managers;
using Mediachase.Commerce.Orders.Search;

namespace Klarna.Payments
{
    [ServiceConfiguration(typeof(IKlarnaService))]
    public class KlarnaService : IKlarnaService
    {
        private readonly IKlarnaServiceApi _klarnaServiceApi;
        private readonly IOrderGroupTotalsCalculator _orderGroupTotalsCalculator;
        private readonly ILogger _logger = LogManager.GetLogger(typeof(KlarnaService));
        private readonly IOrderRepository _orderRepository;
        private readonly ReferenceConverter _referenceConverter;
        private readonly UrlResolver _urlResolver;
        private readonly IContentRepository _contentRepository;
        private readonly ICurrentMarket _currentMarket;
        private readonly SessionBuilder _sessionBuilder;
        private readonly IOrderNumberGenerator _orderNumberGenerator;

        public KlarnaService(IKlarnaServiceApi klarnaServiceApi, 
            IOrderGroupTotalsCalculator orderGroupTotalsCalculator, 
            IOrderRepository orderRepository, 
            ReferenceConverter referenceConverter, 
            UrlResolver urlResolver, 
            IContentRepository contentRepository,
            ICurrentMarket currentMarket,
            SessionBuilder sessionBuilder,
            IOrderNumberGenerator orderNumberGenerator)
        {
            _klarnaServiceApi = klarnaServiceApi;
            _orderGroupTotalsCalculator = orderGroupTotalsCalculator;
            _orderRepository = orderRepository;
            _referenceConverter = referenceConverter;
            _urlResolver = urlResolver;
            _contentRepository = contentRepository;
            _currentMarket = currentMarket;
            _sessionBuilder = sessionBuilder;
            _orderNumberGenerator = orderNumberGenerator;
        }

        public async Task<string> CreateOrUpdateSession(ICart cart)
        {
            var test = ServiceLocator.Current.GetInstance<IOrderNumberGenerator>();
            var sessionRequest = _sessionBuilder.Build(GetSessionRequest(cart), cart, GetConfiguration());

            // If the pre assessment is not enabled then don't send the customer information to Klarna
            if (!IsCustomerPreAssessmentEnabled())
            {
                sessionRequest.Customer = null;
            }
            else if (sessionRequest.Customer == null)
            {
                throw new ArgumentNullException("Session.Customer", "Provide customer information when the pre-assessment configuration is enabled in Commerce Manager");
            }
            
            var sessionId = cart.Properties[Constants.KlarnaSessionIdField]?.ToString();
            if (!string.IsNullOrEmpty(sessionId))
            {
                try
                {
                    await _klarnaServiceApi.UpdateSession(sessionId, sessionRequest).ConfigureAwait(false);

                    return cart.Properties[Constants.KlarnaClientTokenField]?.ToString();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex.Message);
                }
            }
            return await CreateSession(sessionRequest, cart);
        }

        public async Task<bool> UpdateBillingAddress(ICart cart, Address address)
        {
            var sessionId = GetSessionId(cart);
            var session = await GetSession(sessionId);
            session.BillingAddress = address;

            try
            {
                await _klarnaServiceApi.UpdateSession(sessionId, session).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
                return false;
            }
        }

        public async Task<bool> UpdateShippingAddress(ICart cart, Address address)
        {
            var sessionId = GetSessionId(cart);
            var session = await GetSession(sessionId);
            session.ShippingAddress = address;

            try
            {
                await _klarnaServiceApi.UpdateSession(sessionId, session).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
                return false;
            }
        }

        public string GetClientToken(ICart cart)
        {
            return cart.Properties[Constants.KlarnaClientTokenField]?.ToString();
        }

        public string GetSessionId(ICart cart)
        {
            return cart.Properties[Constants.KlarnaSessionIdField]?.ToString();
        }

        public async Task<Session> GetSession(string sessionId)
        {
            return await _klarnaServiceApi.GetSession(sessionId).ConfigureAwait(false);
        }

        public async Task<Authorization> GetAuthorizationModel(string sessionId)
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                try
                {
                    var result =  await _klarnaServiceApi.GetSession(sessionId).ConfigureAwait(false);

                    var model = new Authorization
                    {
                        PurchaseCountry = result.PurchaseCountry,
                        PurchaseCurrency = result.PurchaseCurrency,
                        Locale = result.Locale,
                        BillingAddress = result.BillingAddress,
                        ShippingAddress = result.ShippingAddress,
                        OrderAmount = result.OrderAmount,
                        OrderTaxAmount = result.OrderTaxAmount,
                        OrderLines = result.OrderLines,
                        Customer = result.Customer,
                        MerchantReference1 = result.MerchantReference1,
                        MerchantReference2 = result.MerchantReference2,
                        MerchantData = result.MerchantData,
                        Body = result.Body
                    };
                    return model;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex.Message);
                }
            }
            return new Authorization();
        }

        public async Task<CreateOrderResponse> CreateOrder(string authorizationToken, IOrderGroup cart)
        {
            try
            {
                var sessionId = cart.Properties[Constants.KlarnaSessionIdField]?.ToString();
                if (!string.IsNullOrEmpty(sessionId))
                {
                    var session = await GetSession(sessionId);
                    session.MerchantReference1 = _orderNumberGenerator.GenerateOrderNumber(cart);

                    return await _klarnaServiceApi.CreateOrder(authorizationToken, session).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
            }
            return null;
        }

        public async Task CancelAuthorization(string authorizationToken)
        {
            try
            {
                await _klarnaServiceApi.CancelAuthorization(authorizationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
            }
        }

        public bool IsCustomerPreAssessmentEnabled()
        {
            var paymentMethod = PaymentManager.GetPaymentMethodBySystemName(Constants.KlarnaPaymentSystemKeyword, ContentLanguage.PreferredCulture.Name);
            if (paymentMethod != null)
            {
                // If the pre assessment is not enabled then don't send the customer information to Klarna
                return bool.Parse(paymentMethod.GetParameter(Constants.PreAssesmentField, "false"));
            }
            return false;
        }

        public WidgetColorOptions GetWidgetColorOptions()
        {
            var paymentMethod = PaymentManager.GetPaymentMethodBySystemName(Constants.KlarnaPaymentSystemKeyword, ContentLanguage.PreferredCulture.Name);
            if (paymentMethod == null)
            {
                return new WidgetColorOptions();
            }
            return WidgetColorOptions.FromPaymentMethod(paymentMethod);
        }

        private Session GetSessionRequest(ICart cart)
        {
            var request = new Session();
            request.PurchaseCountry = CountryCodeHelper.GetTwoLetterCountryCode(cart.Market.Countries.FirstOrDefault());

            var sendProductUrl = false;
            var paymentMethod = PaymentManager.GetPaymentMethodBySystemName(Constants.KlarnaPaymentSystemKeyword, ContentLanguage.PreferredCulture.Name);
            if (paymentMethod != null)
            {
                request.MerchantUrl = new MerchantUrl
                {
                    Confirmation = paymentMethod.GetParameter(Constants.ConfirmationUrlField),
                    Notification = paymentMethod.GetParameter(Constants.NotificationUrlField),
                };
                request.Options = GetWidgetOptions(paymentMethod);

                sendProductUrl = bool.Parse(paymentMethod.GetParameter(Constants.SendProductAndImageUrlField, "false"));
            }

            var totals = _orderGroupTotalsCalculator.GetTotals(cart);

            var shipment = cart.GetFirstShipment();

            if (shipment != null && shipment.ShippingAddress != null)
            {
                request.ShippingAddress = shipment.ShippingAddress.ToAddress();
            }
            request.OrderAmount = GetAmount(totals.Total);

            request.PurchaseCurrency = cart.Currency.CurrencyCode;
            
            request.Locale = ContentLanguage.PreferredCulture.Name;
            
            var list = new List<OrderLine>();
            foreach (var item in cart.GetAllLineItems())
            {
                var orderLine = GetOrderLine(item, cart.Currency, sendProductUrl);

                list.Add(orderLine);
            }
            if (totals.ShippingTotal.Amount > 0)
            {
                list.Add(new OrderLine
                {
                    Name = "Shipping method",
                    Quantity = 1,
                    UnitPrice = GetAmount(totals.ShippingTotal.Amount),
                    TotalAmount = GetAmount(totals.ShippingTotal.Amount)
                });
            }
            request.OrderLines = list.ToArray();
            
            return request;
        }

        public void FraudUpdate(NotificationModel notification)
        {
            OrderSearchOptions searchOptions = new OrderSearchOptions();
            searchOptions.CacheResults = false; 
            searchOptions.StartingRecord = 0; 
            searchOptions.RecordsToRetrieve = 1;
            searchOptions.Classes = new System.Collections.Specialized.StringCollection { "PurchaseOrder" };
            searchOptions.Namespace = "Mediachase.Commerce.Orders";

            var parameters = new OrderSearchParameters();
            parameters.SqlMetaWhereClause = $"META.{Constants.KlarnaOrderIdField} = '{notification.OrderId}'";

            var purchaseOrder = OrderContext.Current.FindPurchaseOrders(parameters, searchOptions)?.FirstOrDefault();

            if (purchaseOrder != null)
            {
                var order = _orderRepository.Load<IPurchaseOrder>(purchaseOrder.OrderGroupId);
                if (order != null)
                {
                    var orderForm = order.GetFirstForm();
                    var payment = orderForm.Payments.FirstOrDefault();
                    if (payment != null)
                    {
                        payment.Properties[Constants.FraudStatusPaymentMethodField] = notification.EventType;

                        if (notification.EventType.Equals(Models.OrderStatus.ORDER_ACCEPTED.ToString(), StringComparison.InvariantCultureIgnoreCase))
                        {
                            payment.Status = PaymentStatus.Pending.ToString();

                        }
                        else if (notification.EventType.Equals(Models.OrderStatus.ORDER_REJECTED.ToString(), StringComparison.InvariantCultureIgnoreCase))
                        {
                            payment.Status = PaymentStatus.Failed.ToString();
                        }
                    }
                }
            }
        }

        public void RedirectToConfirmationUrl(IPurchaseOrder purchaseOrder)
        {
            if (purchaseOrder == null)
            {
                throw new ArgumentNullException(nameof(purchaseOrder));
            }
            var orderForm = purchaseOrder.GetFirstForm();
            if (orderForm != null)
            {
                var payment = orderForm.Payments.FirstOrDefault(x => x.PaymentMethodName.Equals(Constants.KlarnaPaymentSystemKeyword));
                if (payment != null)
                {
                    var url = payment.Properties[Constants.KlarnaConfirmationUrlField]?.ToString();
                    if (!string.IsNullOrEmpty(url))
                    {
                        HttpContext.Current.Response.Redirect(url);
                    }
                }
            }
        }

        private async Task<string> CreateSession(Session sessionRequest, ICart cart)
        {
            try
            {
                var response = await _klarnaServiceApi.CreatNewSession(sessionRequest).ConfigureAwait(false);

                cart.Properties[Constants.KlarnaSessionIdField] = response.SessionId;
                cart.Properties[Constants.KlarnaClientTokenField] = response.ClientToken;

                _orderRepository.Save(cart);

                return response.ClientToken;
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
            }
            return string.Empty;
        }

        private int GetAmount(decimal money)
        {
            if (money > 0)
            {
                return (int)(money * 100);
            }
            return 0;
        }

        private int GetAmount(Money money)
        {
            if (money.Amount > 0)
            {
                return (int)(money.Amount * 100);
            }
            return 0;
        }

        private string GetVariantImage(ContentReference contentReference)
        {
            VariationContent variant;
            if (_contentRepository.TryGet(contentReference, out variant))
            {
                return variant.CommerceMediaCollection.Select(media => _urlResolver.GetUrl(media.AssetLink)).FirstOrDefault();
            }
            return string.Empty;
        }

        private Options GetWidgetOptions(PaymentMethodDto paymentMethod)
        {
            var options = new Options();

            options.ColorDetails = paymentMethod.GetParameter(Constants.KlarnaWidgetColorDetailsField, "#C0FFEE");
            options.ColorButton = paymentMethod.GetParameter(Constants.KlarnaWidgetColorButtonField, "#C0FFEE");
            options.ColorButtonText = paymentMethod.GetParameter(Constants.KlarnaWidgetColorButtonTextField, "#C0FFEE");
            options.ColorCheckbox = paymentMethod.GetParameter(Constants.KlarnaWidgetColorCheckboxField, "#C0FFEE");
            options.ColorCheckboxCheckmark =
                paymentMethod.GetParameter(Constants.KlarnaWidgetColorCheckboxCheckmarkField, "#C0FFEE");
            options.ColorHeader = paymentMethod.GetParameter(Constants.KlarnaWidgetColorHeaderField, "#C0FFEE");
            options.ColorLink = paymentMethod.GetParameter(Constants.KlarnaWidgetColorLinkField, "#C0FFEE");
            options.ColorBorder = paymentMethod.GetParameter(Constants.KlarnaWidgetColorBorderField, "#C0FFEE");
            options.ColorBorderSelected = paymentMethod.GetParameter(Constants.KlarnaWidgetColorBorderSelectedField,
                "#C0FFEE");
            options.ColorText = paymentMethod.GetParameter(Constants.KlarnaWidgetColorTextField, "#C0FFEE");
            options.ColorTextSecondary = paymentMethod.GetParameter(Constants.KlarnaWidgetColorTextSecondaryField,
                "#C0FFEE");
            options.RadiusBorder = paymentMethod.GetParameter(Constants.KlarnaWidgetRadiusBorderField, "#0px");

            return options;
        }

        private OrderLine GetOrderLine(ILineItem item, Currency currency, bool sendProductUrl)
        {
            var orderLine = new OrderLine();
            orderLine.Quantity = (int)item.Quantity;
            orderLine.Name = item.DisplayName;
            orderLine.Reference = item.Code;

            //var isDiscount = item.GetDiscountedPrice(currency).Amount > 0;

            orderLine.UnitPrice = GetAmount(item.PlacedPrice);
            
            orderLine.TotalDiscountAmount = GetAmount(item.GetEntryDiscount());
            orderLine.TotalAmount = GetAmount(item.GetExtendedPrice(currency).Amount);

            if (sendProductUrl)
            {
                var contentLink = _referenceConverter.GetContentLink(item.Code);
                if (!ContentReference.IsNullOrEmpty(contentLink))
                {
                    orderLine.ProductUrl = _urlResolver.GetUrl(contentLink);
                    orderLine.ProductImageUrl = GetVariantImage(contentLink);
                }
            }
            return orderLine;
        }

        private Configuration GetConfiguration()
        {
            var configuration = new Configuration();

            var paymentMethod = PaymentManager.GetPaymentMethodBySystemName(Constants.KlarnaPaymentSystemKeyword, ContentLanguage.PreferredCulture.Name);
            if (paymentMethod != null)
            {
                configuration.IsCustomerPreAssessmentEnabled = bool.Parse(paymentMethod.GetParameter(Constants.PreAssesmentField, "false"));
                configuration.SendProductAndImageUrlField = bool.Parse(paymentMethod.GetParameter(Constants.SendProductAndImageUrlField, "false"));
                configuration.UseAttachmentsField = bool.Parse(paymentMethod.GetParameter(Constants.UseAttachmentsField, "false"));
            }
            return configuration;
        }
    }
}

