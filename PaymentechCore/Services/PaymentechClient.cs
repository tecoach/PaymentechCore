using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentechCore.Models;
using PaymentechCore.Models.RequestModels;
using PaymentechCore.Models.ResponseModels;
using System.Net.Http.Headers;
using System.Text;
using System.Net;

namespace PaymentechCore.Services
{
    public class PaymentechClientOptions
    {
        public string InterfaceVersion { get; set; }
        public Credentials Credentials { get; set; }
        public bool Production { get; set; }
    }

    public class PaymentechClient : IPaymentechClient
    {
        static readonly long MaxTraceNumber = 9999999999999999;
        readonly PaymentechClientOptions _options;
        readonly Endpoint _endpoint;
        readonly IPaymentechCache _cache;
        readonly ILogger _logger;

        public PaymentechClient(IOptions<PaymentechClientOptions> optionsAccessor)
        {
            _options = optionsAccessor.Value;
            _endpoint = new Endpoint(_options.Credentials, _options.Production);
        }

        public PaymentechClient(IOptions<PaymentechClientOptions> optionsAccessor, IPaymentechCache cache) : this(optionsAccessor)
        {
            _cache = cache;
        }

        public PaymentechClient(IOptions<PaymentechClientOptions> optionsAccessor, ILogger<PaymentechClient> logger) : this(optionsAccessor)
        {
            _logger = logger;
        }

        public PaymentechClient(IOptions<PaymentechClientOptions> optionsAccessor, IPaymentechCache cache, ILogger<PaymentechClient> logger) : this(optionsAccessor)
        {
            _cache = cache;
            _logger = logger;
        }

        string ClientRequestToContent(ClientRequest clientRequest)
        {
            var requestBody = "";
            var requestSerializer = new XmlSerializer(typeof(Request));
            using (var stream = new MemoryStream())
            using (var writer = new StreamWriter(stream))
            {
                var ns = new XmlSerializerNamespaces();
                ns.Add("", "");
                requestSerializer.Serialize(writer, clientRequest.Request, ns);

                stream.Position = 0;
                using var reader = new StreamReader(stream);
                requestBody = reader.ReadToEnd();
            }

            if (string.IsNullOrEmpty(requestBody))
            {
                throw new Exception("Request body is empty");
            }

            return requestBody;
        }

        string ClientResponseToContent(ClientResponse clientResponse)
        {
            var responseBody = "";
            var responseSerializer = new XmlSerializer(typeof(Response));
            using (var stream = new MemoryStream())
            using (var writer = new StreamWriter(stream))
            {
                var ns = new XmlSerializerNamespaces();
                ns.Add("", "");
                responseSerializer.Serialize(writer, clientResponse.Response, ns);

                stream.Position = 0;
                using var reader = new StreamReader(stream);
                responseBody = reader.ReadToEnd();
            }

            if (string.IsNullOrEmpty(responseBody))
            {
                throw new Exception("Response body is empty");
            }

            return responseBody;
        }

        ClientResponse ContentToClientResponse(string content, string traceNumber, bool previousRequest = false)
        {
            var responseSerializer = new XmlSerializer(typeof(Response));
            using var reader = new StringReader(content);
            Response response = (Response)responseSerializer.Deserialize(reader);
            return new ClientResponse
            {
                Response = response,
                TraceNumber = traceNumber,
                PreviousRequest = previousRequest,
            };
        }

        ClientRequest ScrubClientRequest(ClientRequest clientRequest)
        {
            var scrubbedClientRequest = clientRequest.DeepCopy();

            if (scrubbedClientRequest?.Request?.Item == null)
            {
                return scrubbedClientRequest;
            }
            if (scrubbedClientRequest.Request.Item is BaseElementsType)
            {
                var item = scrubbedClientRequest.Request.Item as BaseElementsType;
                item.AccountNum = "";
                item.CAVV = "";
                item.CardSecVal = "";
                item.Exp = "";
            }
            if (scrubbedClientRequest.Request.Item is ProfileType)
            {
                var item = scrubbedClientRequest.Request.Item as ProfileType;
                item.CCAccountNum = "";
                item.CCExpireDate = "";
            }
            if (scrubbedClientRequest.Request.Item is FlexCacheType)
            {
                var item = scrubbedClientRequest.Request.Item as FlexCacheType;
                item.AccountNum = "";
                item.CardSecVal = "";
            }
            if (scrubbedClientRequest.Request.Item is NewOrderType)
            {
                var item = scrubbedClientRequest.Request.Item as NewOrderType;
                item.AccountNum = "";
                item.CAVV = "";
                item.CardSecVal = "";
                item.Exp = "";
            }

            return scrubbedClientRequest;
        }

        ClientResponse ScrubClientResponse(ClientResponse clientResponse)
        {
            var scrubbedClientResponse = clientResponse.DeepCopy();
            if (scrubbedClientResponse?.Response?.Item == null)
            {
                return scrubbedClientResponse;
            }
            if (scrubbedClientResponse.Response.Item is SafetechFraudAnalysisRespType)
            {
                var item = scrubbedClientResponse.Response.Item as SafetechFraudAnalysisRespType;
                item.AccountNum = "";
            }
            if (scrubbedClientResponse.Response.Item is InquiryRespType)
            {
                var item = scrubbedClientResponse.Response.Item as InquiryRespType;
                item.AccountNum = "";
            }
            if (scrubbedClientResponse.Response.Item is QuickRespType_Old)
            {
                var item = scrubbedClientResponse.Response.Item as QuickRespType_Old;
                item.AccountNum = "";
            }
            if (scrubbedClientResponse.Response.Item is QuickRespType)
            {
                var item = scrubbedClientResponse.Response.Item as QuickRespType;
                item.AccountNum = "";
                item.CCAccountNum = "";
                item.CCExpireDate = "";
            }
            if (scrubbedClientResponse.Response.Item is ProfileRespType)
            {
                var item = scrubbedClientResponse.Response.Item as ProfileRespType;
                item.CCAccountNum = "";
                item.CCExpireDate = "";
            }
            if (scrubbedClientResponse.Response.Item is NewOrderRespType)
            {
                var item = scrubbedClientResponse.Response.Item as NewOrderRespType;
                item.AccountNum = "";
            }
            if (scrubbedClientResponse.Response.Item is FlexCacheRespType)
            {
                var item = scrubbedClientResponse.Response.Item as FlexCacheRespType;
                // item.Item can be AccountNum, StartAccountNum, ItemElementName
                item.Item = "";
            }
            
            return scrubbedClientResponse;
        }

        async Task<ClientResponse> SendRequestAsync(string url, ClientRequest clientRequest)
        {
            if (string.IsNullOrEmpty(clientRequest.TraceNumber))
            {
                clientRequest.TraceNumber = NewTraceNumber();
            }
            else
            {
                if (!long.TryParse(clientRequest.TraceNumber, out long traceNumberVal))
                {
                    throw new Exception("Trace number must convert to int64");
                }
                if (traceNumberVal > MaxTraceNumber)
                {
                    throw new Exception("Trace number larger then accepted maximum");
                }
                if (_cache != null)
                {
                    var previousResponseContent = _cache.GetValue(clientRequest.TraceNumber);
                    if (!string.IsNullOrEmpty(previousResponseContent))
                    {
                        var previousClientResponse = ContentToClientResponse(previousResponseContent, clientRequest.TraceNumber, true);
                        return previousClientResponse;
                    }
                }
            }

            var headers = new Headers(clientRequest.TraceNumber, _options.InterfaceVersion, _options.Credentials.MerchantId);

            var contentType = headers.ContentType();
            using var client = new HttpClient
            {
                BaseAddress = new Uri(url)
            };
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("MIME-Version", headers.MIME_Version);
            client.DefaultRequestHeaders.Add("Content-transfer-encoding", headers.ContentTransferEncoding);
            client.DefaultRequestHeaders.Add("Request-number", headers.RequestNumber);
            client.DefaultRequestHeaders.Add("Document-type", headers.DocumentType);
            client.DefaultRequestHeaders.Add("Trace-number", headers.TraceNumber);
            client.DefaultRequestHeaders.Add("Interface-version", headers.InterfaceVersion);
            client.DefaultRequestHeaders.Add("MerchantID", headers.MerchantID);

            var scrubbedRequest = ScrubClientRequest(clientRequest);
            var scrubbedRequestContent = ClientRequestToContent(scrubbedRequest);

            var requestBody = ClientRequestToContent(clientRequest);
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "")
            {
                Content = new StringContent(requestBody)
            };
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            var httpResponse = await client.SendAsync(httpRequest);

            var httpResponseContent = await httpResponse.Content.ReadAsStringAsync();

            if (string.IsNullOrEmpty(httpResponseContent))
            {
                throw new Exception("Response content is empty");
            }

            var clientResponse = ContentToClientResponse(httpResponseContent, clientRequest.TraceNumber);

            var scrubbedResponse = ScrubClientResponse(clientResponse);
            var scrubbedResponseContent = ClientResponseToContent(scrubbedResponse);

            if (_cache != null)
            {
                _cache.SetValue(clientRequest.TraceNumber, scrubbedResponseContent);
            }

            var itemType = ResponseTypes.Types[clientResponse.Response.Item.GetType()];

            string procStatus = null;
            switch (itemType)
            {
                case (int)ResponeTypeIds.AccountUpdaterResp:
                    var accountUpdater = (AccountUpdaterRespType)clientResponse.Response.Item;
                    procStatus = accountUpdater.ProfileProcStatus;
                    break;
                case (int)ResponeTypeIds.EndOfDayResp:
                    var endOfDay = (EndOfDayRespType)clientResponse.Response.Item;
                    procStatus = endOfDay.ProcStatus;
                    break;
                case (int)ResponeTypeIds.FlexCacheResp:
                    var flexCache = (FlexCacheRespType)clientResponse.Response.Item;
                    procStatus = flexCache.ProcStatus;
                    break;
                case (int)ResponeTypeIds.InquiryResp:
                    var inquiry = (InquiryRespType)clientResponse.Response.Item;
                    procStatus = inquiry.ProcStatus;
                    break;
                case (int)ResponeTypeIds.MarkForCaptureResp:
                    var markForCapture = (MarkForCaptureRespType)clientResponse.Response.Item;
                    procStatus = markForCapture.ProcStatus;
                    break;
                case (int)ResponeTypeIds.NewOrderResp:
                    var newOrder = (NewOrderRespType)clientResponse.Response.Item;
                    procStatus = newOrder.ProcStatus;
                    break;
                case (int)ResponeTypeIds.ProfileResp:
                    var profile = (ProfileRespType)clientResponse.Response.Item;
                    procStatus = profile.ProfileProcStatus;
                    break;
                case (int)ResponeTypeIds.QuickResp:
                    var quick = (QuickRespType)clientResponse.Response.Item;
                    procStatus = quick.ProcStatus;
                    break;
                case (int)ResponeTypeIds.QuickResponse:
                    var quick_old = (QuickRespType_Old)clientResponse.Response.Item;
                    procStatus = quick_old.ProcStatus;
                    break;
                case (int)ResponeTypeIds.ReversalResp:
                    var reversal = (ReversalRespType)clientResponse.Response.Item;
                    procStatus = reversal.ProcStatus;
                    break;
                case (int)ResponeTypeIds.SafetechFraudAnalysisResp:
                    var safetechFraudAnalysis = (SafetechFraudAnalysisRespType)clientResponse.Response.Item;
                    procStatus = safetechFraudAnalysis.ProcStatus;
                    break;
                default:
                    break;
            }
            if (_logger != null)
            {
                if (procStatus != "0")
                {
                    _logger.LogWarning($"Request-{clientRequest.TraceNumber}: {scrubbedRequestContent}");
                    _logger.LogWarning($"Response-{clientRequest.TraceNumber}: {scrubbedResponseContent}");
                }
                else
                {
                    _logger.LogInformation($"Request-{clientRequest.TraceNumber}: {scrubbedRequestContent}");
                    _logger.LogInformation($"Response-{clientRequest.TraceNumber}: {scrubbedResponseContent}");
                }
            }
            clientResponse.ProcStatus = procStatus;

            return clientResponse;
        }

        public Credentials Credentials()
        {
            return _options?.Credentials;
        }

        public string InterfaceVersion()
        {
            return _options?.InterfaceVersion;
        }

        public IPaymentechCache GetCache()
        {
            return _cache;
        }

        public string NewTraceNumber()
        {
            // use Guid to generate a unique id,
            // and it's hash code to convert it to numeric format
            var newTrace = Guid.NewGuid().GetHashCode();
            // make sure that the hash is positive
            if (newTrace < 0)
            {
                newTrace *= -1;
            }
            // if the numeric format is too large,
            // a substring should be roughly unique enough
            var newTraceStr = newTrace.ToString();
            var maxLength = MaxTraceNumber.ToString().Length;
            if (newTraceStr.Length > maxLength)
            {
                newTraceStr = newTraceStr.Substring(0, maxLength - 1);
            }
            return newTraceStr;
        }

        public ClientResponse UpdateAccount(AccountUpdaterType accountUpdate, string traceNumber = null)
        {
            return UpdateAccountAsync(accountUpdate, traceNumber).GetAwaiter().GetResult();
        }

        public async Task<ClientResponse> UpdateAccountAsync(AccountUpdaterType accountUpdate, string traceNumber = null)
        {
            var xmlBody = new Request { Item = accountUpdate };
            var url = _endpoint.Url();
            var request = new ClientRequest
            {
                Request = xmlBody,
                TraceNumber = traceNumber,
            };
            return await SendRequestAsync(url, request);
        }

        public ClientResponse EndOfDay(EndOfDayType endOfDay, string traceNumber = null)
        {
            return EndOfDayAsync(endOfDay, traceNumber).GetAwaiter().GetResult();
        }

        public async Task<ClientResponse> EndOfDayAsync(EndOfDayType endOfDay, string traceNumber = null)
        {
            var xmlBody = new Request { Item = endOfDay };
            var url = _endpoint.Url();
            var request = new ClientRequest
            {
                Request = xmlBody,
                TraceNumber = traceNumber,
            };
            return await SendRequestAsync(url, request);
        }

        public ClientResponse FlexCache(FlexCacheType flexCache, string traceNumber = null)
        {
            return FlexCacheAsync(flexCache, traceNumber).GetAwaiter().GetResult();
        }

        public async Task<ClientResponse> FlexCacheAsync(FlexCacheType flexCache, string traceNumber = null)
        {
            var xmlBody = new Request { Item = flexCache };
            var url = _endpoint.Url();
            var request = new ClientRequest
            {
                Request = xmlBody,
                TraceNumber = traceNumber,
            };
            return await SendRequestAsync(url, request);
        }

        public ClientResponse Inquiry(InquiryType inquiry, string traceNumber = null)
        {
            return InquiryAsync(inquiry, traceNumber).GetAwaiter().GetResult();
        }
        
        public async Task<ClientResponse> InquiryAsync(InquiryType inquiry, string traceNumber = null)
        {
            var xmlBody = new Request { Item = inquiry };
            var url = _endpoint.Url();
            var request = new ClientRequest
            {
                Request = xmlBody,
                TraceNumber = traceNumber,
            };
            return await SendRequestAsync(url, request);
        }

        public ClientResponse MarkForCapture(MarkForCaptureType markForCapture, string traceNumber = null)
        {
            return MarkForCaptureAsync(markForCapture, traceNumber).GetAwaiter().GetResult();
        }

        public async Task<ClientResponse> MarkForCaptureAsync(MarkForCaptureType markForCapture, string traceNumber = null)
        {
            var xmlBody = new Request { Item = markForCapture };
            var url = _endpoint.Url();
            var request = new ClientRequest
            {
                Request = xmlBody,
                TraceNumber = traceNumber,
            };
            return await SendRequestAsync(url, request);
        }

        public ClientResponse NewOrder(NewOrderType newOrder, string traceNumber = null)
        {
            return NewOrderAsync(newOrder, traceNumber).GetAwaiter().GetResult();
        }

        public async Task<ClientResponse> NewOrderAsync(NewOrderType newOrder, string traceNumber = null)
        {
            var xmlBody = new Request { Item = newOrder };
            var url = _endpoint.Url();
            var request = new ClientRequest
            {
                Request = xmlBody,
                TraceNumber = traceNumber,
            };
            return await SendRequestAsync(url, request);
        }

        public ClientResponse Profile(ProfileType profile, string traceNumber = null)
        {
            return ProfileAsync(profile, traceNumber).GetAwaiter().GetResult();
        }

        public async Task<ClientResponse> ProfileAsync(ProfileType profile, string traceNumber = null)
        {
            var xmlBody = new Request { Item = profile };
            var url = _endpoint.Url();
            var request = new ClientRequest
            {
                Request = xmlBody,
                TraceNumber = traceNumber,
            };
            return await SendRequestAsync(url, request);
        }

        public ClientResponse Reversal(ReversalType reversal, string traceNumber = null)
        {
            return ReversalAsync(reversal, traceNumber).GetAwaiter().GetResult();
        }

        public async Task<ClientResponse> ReversalAsync(ReversalType reversal, string traceNumber = null)
        {
            var xmlBody = new Request { Item = reversal };
            var url = _endpoint.Url();
            var request = new ClientRequest
            {
                Request = xmlBody,
                TraceNumber = traceNumber,
            };
            return await SendRequestAsync(url, request);
        }

        public ClientResponse SafetechFraudAnalysis(SafetechFraudAnalysisType safetechFraudAnalysis, string traceNumber = null)
        {
            return SafetechFraudAnalysisAsync(safetechFraudAnalysis, traceNumber).GetAwaiter().GetResult();
        }

        public async Task<ClientResponse> SafetechFraudAnalysisAsync(SafetechFraudAnalysisType safetechFraudAnalysis, string traceNumber = null)
        {
            var xmlBody = new Request { Item = safetechFraudAnalysis };
            var url = _endpoint.Url();
            var request = new ClientRequest
            {
                Request = xmlBody,
                TraceNumber = traceNumber,
            };
            return await SendRequestAsync(url, request);
        }
    }
}