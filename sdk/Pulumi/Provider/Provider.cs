using Pulumirpc;
using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Pulumi.Experimental.Provider
{
    public sealed class CheckRequest
    {
        public readonly string Urn;
        // Note the Go SDK directly exposes resource.URN and so providers can work with it directly. I've
        // decided _not_ to copy that to the dotnet SDK on the basis that long term I'd like URNs to be opaque
        // tokens to everything but the engine. If CheckRequests need the resource type and name they should
        // be sent as separate string fields by the engine, rather than expecting every language to correctly
        // parse URNs. But for now we're half-waying this by having the public dotnet API expose Type and Name
        // directly, but by parsing the single URN sent from the engine.
        public string Type => Pulumi.Urn.Type(Urn);
        public string Name => Pulumi.Urn.Name(Urn);
        public readonly ImmutableDictionary<string, PropertyValue> OldInputs;
        public readonly ImmutableDictionary<string, PropertyValue> NewInputs;
        public readonly ImmutableArray<byte> RandomSeed;

        public CheckRequest(string urn, ImmutableDictionary<string, PropertyValue> oldInputs, ImmutableDictionary<string, PropertyValue> newInputs, ImmutableArray<byte> randomSeed)
        {
            Urn = urn;
            OldInputs = oldInputs;
            NewInputs = newInputs;
            RandomSeed = randomSeed;
        }
    }

    public sealed class CheckFailure
    {
        public string Property { get; set; }
        public string Reason { get; set; }

        public CheckFailure(string property, string reason)
        {
            Property = property;
            Reason = reason;
        }
    }

    public sealed class CheckResponse
    {
        public IDictionary<string, PropertyValue>? Inputs { get; set; }
        public IList<CheckFailure>? Failures { get; set; }
    }


    public sealed class DiffRequest
    {
        public readonly string Urn;
        public string Type => Pulumi.Urn.Type(Urn);
        public string Name => Pulumi.Urn.Name(Urn);
        public readonly string Id;
        public readonly ImmutableDictionary<string, PropertyValue> OldState;
        public readonly ImmutableDictionary<string, PropertyValue> NewInputs;
        public readonly ImmutableArray<string> IgnoreChanges;

        public DiffRequest(string urn, string id, ImmutableDictionary<string, PropertyValue> oldState, ImmutableDictionary<string, PropertyValue> newInputs, ImmutableArray<string> ignoreChanges)
        {
            Urn = urn;
            Id = id;
            OldState = oldState;
            NewInputs = newInputs;
            IgnoreChanges = ignoreChanges;
        }
    }

    public enum PropertyDiffKind
    {
        Add = 0,
        AddReplace = 1,
        Delete = 2,
        DeleteReplace = 3,
        Update = 4,
        UpdateReplace = 5,
    }

    public sealed class PropertyDiff
    {
        public PropertyDiffKind Kind { get; set; }
        public bool InputDiff { get; set; }
    }

    public sealed class DiffResponse
    {
        public bool? Changes { get; set; }

        public IList<string>? Replaces { get; set; }

        public IList<string>? Stables { get; set; }

        public bool DeleteBeforeReplace { get; set; }
        public IList<string>? Diffs { get; set; }

        public IDictionary<string, PropertyDiff>? DetailedDiff { get; set; }
    }

    public sealed class InvokeRequest
    {
        public readonly string Tok;
        public readonly ImmutableDictionary<string, PropertyValue> Args;

        public InvokeRequest(string tok, ImmutableDictionary<string, PropertyValue> args)
        {
            Tok = tok;
            Args = args;
        }
    }

    public sealed class InvokeResponse
    {

        public IDictionary<string, PropertyValue>? Return { get; set; }
        public IList<CheckFailure>? Failures { get; set; }
    }

    public sealed class GetSchemaRequest
    {
        public readonly int Version;

        public GetSchemaRequest(int version)
        {
            Version = version;
        }
    }

    public sealed class GetSchemaResponse
    {
        public string? Schema { get; set; }
    }

    public sealed class ConfigureRequest
    {
        public readonly ImmutableDictionary<string, string> Variables;
        public readonly ImmutableDictionary<string, PropertyValue> Args;
        public readonly bool AcceptSecrets;
        public readonly bool AcceptResources;

        public ConfigureRequest(ImmutableDictionary<string, string> variables, ImmutableDictionary<string, PropertyValue> args, bool acceptSecrets, bool acceptResources)
        {
            Variables = variables;
            Args = args;
            AcceptSecrets = acceptSecrets;
            AcceptResources = acceptResources;
        }
    }

    public sealed class ConfigureResponse
    {
        public bool AcceptSecrets { get; set; }
        public bool SupportsPreview { get; set; }
        public bool AcceptResources { get; set; }
        public bool AcceptOutputs { get; set; }
    }

    public sealed class CreateRequest
    {
        public readonly string Urn;
        public string Type => Pulumi.Urn.Type(Urn);
        public string Name => Pulumi.Urn.Name(Urn);
        public readonly ImmutableDictionary<string, PropertyValue> Properties;
        public readonly TimeSpan Timeout;
        public readonly bool Preview;

        public CreateRequest(string urn, ImmutableDictionary<string, PropertyValue> properties, TimeSpan timeout, bool preview)
        {
            Urn = urn;
            Properties = properties;
            Timeout = timeout;
            Preview = preview;
        }
    }

    public sealed class CreateResponse
    {
        public string? Id { get; set; }
        public IDictionary<string, PropertyValue>? Properties { get; set; }
    }

    public sealed class ReadRequest
    {
        public readonly string Urn;
        public readonly string Id;
        public string Type => Pulumi.Urn.Type(Urn);
        public string Name => Pulumi.Urn.Name(Urn);
        public readonly ImmutableDictionary<string, PropertyValue> Properties;
        public readonly ImmutableDictionary<string, PropertyValue> Inputs;

        public ReadRequest(string urn, string id, ImmutableDictionary<string, PropertyValue> properties, ImmutableDictionary<string, PropertyValue> inputs)
        {
            Urn = urn;
            Id = id;
            Properties = properties;
            Inputs = inputs;
        }
    }

    public sealed class ReadResponse
    {
        public string? Id { get; set; }
        public IDictionary<string, PropertyValue>? Properties { get; set; }
        public IDictionary<string, PropertyValue>? Inputs { get; set; }
    }

    public sealed class UpdateRequest
    {
        public readonly string Urn;
        public readonly string Id;
        public string Type => Pulumi.Urn.Type(Urn);
        public string Name => Pulumi.Urn.Name(Urn);
        public readonly ImmutableDictionary<string, PropertyValue> Olds;
        public readonly ImmutableDictionary<string, PropertyValue> News;
        public readonly TimeSpan Timeout;
        public readonly ImmutableArray<string> IgnoreChanges;
        public readonly bool Preview;

        public UpdateRequest(string urn, string id, ImmutableDictionary<string, PropertyValue> olds, ImmutableDictionary<string, PropertyValue> news, TimeSpan timeout, ImmutableArray<string> ignoreChanges, bool preview)
        {
            Urn = urn;
            Id = id;
            Olds = olds;
            News = news;
            Timeout = timeout;
            IgnoreChanges = ignoreChanges;
            Preview = preview;
        }
    }

    public sealed class UpdateResponse
    {
        public IDictionary<string, PropertyValue>? Properties { get; set; }
    }

    public sealed class DeleteRequest
    {
        public readonly string Urn;
        public readonly string Id;
        public string Type => Pulumi.Urn.Type(Urn);
        public string Name => Pulumi.Urn.Name(Urn);
        public readonly ImmutableDictionary<string, PropertyValue> Properties;
        public readonly TimeSpan Timeout;

        public DeleteRequest(string urn, string id, ImmutableDictionary<string, PropertyValue> properties, TimeSpan timeout)
        {
            Urn = urn;
            Id = id;
            Properties = properties;
            Timeout = timeout;
        }
    }

    public abstract class Provider
    {
        public virtual Task<GetSchemaResponse> GetSchema(GetSchemaRequest request, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public virtual Task<CheckResponse> CheckConfig(CheckRequest request, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public virtual Task<DiffResponse> DiffConfig(DiffRequest request, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public virtual Task<ConfigureResponse> Configure(ConfigureRequest request, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public virtual Task<InvokeResponse> Invoke(InvokeRequest request, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public virtual Task<CreateResponse> Create(CreateRequest request, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public virtual Task<ReadResponse> Read(ReadRequest request, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public virtual Task<CheckResponse> Check(CheckRequest request, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public virtual Task<DiffResponse> Diff(DiffRequest request, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public virtual Task<UpdateResponse> Update(UpdateRequest request, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public virtual Task Delete(DeleteRequest request, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public static async Task Serve(string[] args, string? version, Func<IHost, Provider> factory, System.Threading.CancellationToken cancellationToken)
        {
            // maxRpcMessageSize raises the gRPC Max message size from `4194304` (4mb) to `419430400` (400mb)
            var maxRpcMessageSize = 400 * 1024 * 1024;

            var host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder
                        .ConfigureKestrel(kestrelOptions =>
                        {
                            kestrelOptions.Listen(IPAddress.Loopback, 0, listenOptions =>
                            {
                                listenOptions.Protocols = HttpProtocols.Http2;
                            });
                        })
                        .ConfigureAppConfiguration((context, config) =>
                        {
                            // clear so we don't read appsettings.json
                            // note that we also won't read environment variables for config
                            config.Sources.Clear();

                            var memConfig = new Dictionary<string, string>();
                            if (args.Length > 0)
                            {
                                memConfig.Add("Host", args[0]);
                            }
                            if (version != null)
                            {
                                memConfig.Add("Version", version);
                            }
                            config.AddInMemoryCollection(memConfig);
                        })
                        .ConfigureLogging(loggingBuilder =>
                        {
                            // disable default logging
                            loggingBuilder.ClearProviders();
                        })
                        .ConfigureServices(services =>
                        {
                            // to be injected into ResourceProviderService
                            services.AddSingleton(factory);
                            services.AddSingleton<ResourceProviderService>();

                            services.AddGrpc(grpcOptions =>
                            {
                                grpcOptions.MaxReceiveMessageSize = maxRpcMessageSize;
                                grpcOptions.MaxSendMessageSize = maxRpcMessageSize;
                            });
                        })
                        .Configure(app =>
                        {
                            app.UseRouting();
                            app.UseEndpoints(endpoints =>
                            {
                                endpoints.MapGrpcService<ResourceProviderService>();
                            });
                        });
                })
                .Build();

            // before starting the host, set up this callback to tell us what port was selected
            var portTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var portRegistration = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
            {
                try
                {
                    var serverFeatures = host.Services.GetRequiredService<IServer>().Features;
                    var addressesFeature = serverFeatures.Get<IServerAddressesFeature>();
                    Debug.Assert(addressesFeature != null, "Server should have an IServerAddressesFeature");
                    var addresses = addressesFeature.Addresses.ToList();
                    Debug.Assert(addresses.Count == 1, "Server should only be listening on one address");
                    var uri = new Uri(addresses[0]);
                    portTcs.TrySetResult(uri.Port);
                }
                catch (Exception ex)
                {
                    portTcs.TrySetException(ex);
                }
            });

            await host.StartAsync(cancellationToken);

            var port = await portTcs.Task;
            // Explicitly write just the number and "\n". WriteLine would write "\r\n" on Windows, and while
            // the engine has now been fixed to handle that (see https://github.com/pulumi/pulumi/pull/11915)
            // we work around this here so that old engines can use dotnet providers as well.
            System.Console.Write(port.ToString() + "\n");

            await host.WaitForShutdownAsync(cancellationToken);

            host.Dispose();
        }
    }

    class ResourceProviderService : ResourceProvider.ResourceProviderBase, IDisposable
    {
        readonly Func<IHost, Provider> factory;
        readonly CancellationTokenSource rootCTS;
        Provider? implementation;
        readonly string version;

        Provider Implementation
        {
            get
            {
                if (implementation == null)
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Engine host not yet attached"));
                }
                return implementation;
            }
        }

        private void CreateProvider(string address)
        {
            var host = new GrpcHost(address);
            implementation = factory(host);
        }

        public ResourceProviderService(Func<IHost, Provider> factory, IConfiguration configuration)
        {
            this.factory = factory;
            this.rootCTS = new CancellationTokenSource();

            var host = configuration.GetValue<string?>("Host", null);
            if (host != null)
            {
                CreateProvider(host);
            }

            var version = configuration.GetValue<string?>("Version", null);
            if (version == null)
            {
                var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
                Debug.Assert(entryAssembly != null, "GetEntryAssembly returned null in managed code");
                var entryName = entryAssembly.GetName();
                var assemblyVersion = entryName.Version;
                if (assemblyVersion != null)
                {
                    version = assemblyVersion.ToString();
                }
                else
                {
                    version = "0.0.1";
                }
            }
            this.version = version;
        }

        public void Dispose()
        {
            this.rootCTS.Dispose();
        }

        public override Task<Empty> Attach(Pulumirpc.PluginAttach request, ServerCallContext context)
        {
            CreateProvider(request.Address);
            return Task.FromResult(new Empty());
        }

        public override Task<Empty> Cancel(Empty request, ServerCallContext context)
        {
            try
            {
                this.rootCTS.Cancel();
                return Task.FromResult(new Empty());
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        private CancellationTokenSource GetToken(ServerCallContext context)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(rootCTS.Token, context.CancellationToken);
        }

        // Helper to deal with the fact that at the GRPC layer any Struct property might be null. For those we just want to return empty dictionaries at this level.
        // This keeps the PropertyValue.Marshal clean in terms of not handling nulls. 
        private ImmutableDictionary<string, PropertyValue> Marshal(Struct? properties)
        {
            if (properties == null)
            {
                return ImmutableDictionary<string, PropertyValue>.Empty;
            }
            return PropertyValue.Marshal(properties);
        }

        public override async Task<Pulumirpc.CheckResponse> CheckConfig(Pulumirpc.CheckRequest request, ServerCallContext context)
        {
            try
            {
                var domRequest = new CheckRequest(request.Urn, Marshal(request.Olds), Marshal(request.News), ImmutableArray.ToImmutableArray(request.RandomSeed));
                using var cts = GetToken(context);
                var domResponse = await Implementation.CheckConfig(domRequest, cts.Token);
                var grpcResponse = new Pulumirpc.CheckResponse();
                grpcResponse.Inputs = domResponse.Inputs == null ? null : PropertyValue.Unmarshal(domResponse.Inputs);
                if (domResponse.Failures != null)
                {
                    foreach (var domFailure in domResponse.Failures)
                    {
                        var grpcFailure = new Pulumirpc.CheckFailure();
                        grpcFailure.Property = domFailure.Property;
                        grpcFailure.Reason = domFailure.Reason;
                        grpcResponse.Failures.Add(grpcFailure);
                    }
                }
                return grpcResponse;
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task<Pulumirpc.DiffResponse> DiffConfig(Pulumirpc.DiffRequest request, ServerCallContext context)
        {
            try
            {
                var domRequest = new DiffRequest(request.Urn, request.Id, Marshal(request.Olds), Marshal(request.News), request.IgnoreChanges.ToImmutableArray());
                using var cts = GetToken(context);
                var domResponse = await Implementation.DiffConfig(domRequest, cts.Token);
                var grpcResponse = new Pulumirpc.DiffResponse();
                if (domResponse.Changes.HasValue)
                {
                    grpcResponse.Changes = domResponse.Changes.Value ? Pulumirpc.DiffResponse.Types.DiffChanges.DiffSome : Pulumirpc.DiffResponse.Types.DiffChanges.DiffNone;
                }
                if (domResponse.Stables != null)
                {
                    grpcResponse.Stables.AddRange(domResponse.Stables);
                }
                if (domResponse.Replaces != null)
                {
                    grpcResponse.Replaces.AddRange(domResponse.Replaces);
                }
                grpcResponse.DeleteBeforeReplace = domResponse.DeleteBeforeReplace;
                if (domResponse.Diffs != null)
                {
                    grpcResponse.Diffs.AddRange(domResponse.Diffs);
                }
                if (domResponse.DetailedDiff != null)
                {
                    foreach (var item in domResponse.DetailedDiff)
                    {
                        var domDiff = item.Value;
                        var grpcDiff = new Pulumirpc.PropertyDiff();
                        grpcDiff.InputDiff = domDiff.InputDiff;
                        grpcDiff.Kind = (Pulumirpc.PropertyDiff.Types.Kind)domDiff.Kind;
                        grpcResponse.DetailedDiff.Add(item.Key, grpcDiff);
                    }
                }
                return grpcResponse;
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task<Pulumirpc.InvokeResponse> Invoke(Pulumirpc.InvokeRequest request, ServerCallContext context)
        {
            try
            {
                var domRequest = new InvokeRequest(request.Tok, Marshal(request.Args));
                using var cts = GetToken(context);
                var domResponse = await Implementation.Invoke(domRequest, cts.Token);
                var grpcResponse = new Pulumirpc.InvokeResponse();
                grpcResponse.Return = domResponse.Return == null ? null : PropertyValue.Unmarshal(domResponse.Return);
                if (domResponse.Failures != null)
                {
                    foreach (var domFailure in domResponse.Failures)
                    {
                        var grpcFailure = new Pulumirpc.CheckFailure();
                        grpcFailure.Property = domFailure.Property;
                        grpcFailure.Reason = domFailure.Reason;
                        grpcResponse.Failures.Add(grpcFailure);
                    }
                }
                return grpcResponse;
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task<Pulumirpc.GetSchemaResponse> GetSchema(Pulumirpc.GetSchemaRequest request, ServerCallContext context)
        {
            try
            {
                var domRequest = new GetSchemaRequest(request.Version);
                using var cts = GetToken(context);
                var domResponse = await Implementation.GetSchema(domRequest, cts.Token);
                var grpcResponse = new Pulumirpc.GetSchemaResponse();
                grpcResponse.Schema = domResponse.Schema ?? "";
                return grpcResponse;
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task<Pulumirpc.ConfigureResponse> Configure(Pulumirpc.ConfigureRequest request, ServerCallContext context)
        {
            try
            {
                var domRequest = new ConfigureRequest(request.Variables.ToImmutableDictionary(), Marshal(request.Args), request.AcceptSecrets, request.AcceptResources);
                using var cts = GetToken(context);
                var domResponse = await Implementation.Configure(domRequest, cts.Token);
                var grpcResponse = new Pulumirpc.ConfigureResponse();
                grpcResponse.AcceptSecrets = domResponse.AcceptSecrets;
                grpcResponse.SupportsPreview = domResponse.SupportsPreview;
                grpcResponse.AcceptResources = domResponse.AcceptResources;
                grpcResponse.AcceptOutputs = domResponse.AcceptOutputs;
                return grpcResponse;
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override Task<Pulumirpc.PluginInfo> GetPluginInfo(Empty request, ServerCallContext context)
        {
            try
            {
                using var cts = GetToken(context);
                var grpcResponse = new Pulumirpc.PluginInfo();
                grpcResponse.Version = this.version;
                return Task.FromResult(grpcResponse);
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task<Pulumirpc.CreateResponse> Create(Pulumirpc.CreateRequest request, ServerCallContext context)
        {
            try
            {
                var domRequest = new CreateRequest(request.Urn, Marshal(request.Properties), TimeSpan.FromSeconds(request.Timeout), request.Preview);
                using var cts = GetToken(context);
                var domResponse = await Implementation.Create(domRequest, cts.Token);
                var grpcResponse = new Pulumirpc.CreateResponse();
                grpcResponse.Id = domResponse.Id ?? "";
                grpcResponse.Properties = domResponse.Properties == null ? null : PropertyValue.Unmarshal(domResponse.Properties);
                return grpcResponse;
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task<Pulumirpc.ReadResponse> Read(Pulumirpc.ReadRequest request, ServerCallContext context)
        {
            try
            {
                var domRequest = new ReadRequest(request.Urn, request.Id, Marshal(request.Properties), Marshal(request.Inputs));
                using var cts = GetToken(context);
                var domResponse = await Implementation.Read(domRequest, cts.Token);
                var grpcResponse = new Pulumirpc.ReadResponse();
                grpcResponse.Id = domResponse.Id ?? "";
                grpcResponse.Properties = domResponse.Properties == null ? null : PropertyValue.Unmarshal(domResponse.Properties);
                grpcResponse.Inputs = domResponse.Inputs == null ? null : PropertyValue.Unmarshal(domResponse.Inputs);
                return grpcResponse;
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task<Pulumirpc.CheckResponse> Check(Pulumirpc.CheckRequest request, ServerCallContext context)
        {
            try
            {
                var domRequest = new CheckRequest(request.Urn, Marshal(request.Olds), Marshal(request.News), ImmutableArray.ToImmutableArray(request.RandomSeed));
                using var cts = GetToken(context);
                var domResponse = await Implementation.Check(domRequest, cts.Token);
                var grpcResponse = new Pulumirpc.CheckResponse();
                grpcResponse.Inputs = domResponse.Inputs == null ? null : PropertyValue.Unmarshal(domResponse.Inputs);
                if (domResponse.Failures != null)
                {
                    foreach (var domFailure in domResponse.Failures)
                    {
                        var grpcFailure = new Pulumirpc.CheckFailure();
                        grpcFailure.Property = domFailure.Property;
                        grpcFailure.Reason = domFailure.Reason;
                        grpcResponse.Failures.Add(grpcFailure);
                    }
                }
                return grpcResponse;
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task<Pulumirpc.DiffResponse> Diff(Pulumirpc.DiffRequest request, ServerCallContext context)
        {
            try
            {
                var domRequest = new DiffRequest(request.Urn, request.Id, Marshal(request.Olds), Marshal(request.News), request.IgnoreChanges.ToImmutableArray());
                using var cts = GetToken(context);
                var domResponse = await Implementation.Diff(domRequest, cts.Token);
                var grpcResponse = new Pulumirpc.DiffResponse();
                if (domResponse.Changes.HasValue)
                {
                    grpcResponse.Changes = domResponse.Changes.Value ? Pulumirpc.DiffResponse.Types.DiffChanges.DiffSome : Pulumirpc.DiffResponse.Types.DiffChanges.DiffNone;
                }
                if (domResponse.Stables != null)
                {
                    grpcResponse.Stables.AddRange(domResponse.Stables);
                }
                if (domResponse.Replaces != null)
                {
                    grpcResponse.Replaces.AddRange(domResponse.Replaces);
                }
                grpcResponse.DeleteBeforeReplace = domResponse.DeleteBeforeReplace;
                if (domResponse.Diffs != null)
                {
                    grpcResponse.Diffs.AddRange(domResponse.Diffs);
                }
                if (domResponse.DetailedDiff != null)
                {
                    foreach (var item in domResponse.DetailedDiff)
                    {
                        var domDiff = item.Value;
                        var grpcDiff = new Pulumirpc.PropertyDiff();
                        grpcDiff.InputDiff = domDiff.InputDiff;
                        grpcDiff.Kind = (Pulumirpc.PropertyDiff.Types.Kind)domDiff.Kind;
                        grpcResponse.DetailedDiff.Add(item.Key, grpcDiff);
                    }
                }
                return grpcResponse;
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task<Pulumirpc.UpdateResponse> Update(Pulumirpc.UpdateRequest request, ServerCallContext context)
        {
            try
            {
                var domRequest = new UpdateRequest(request.Urn, request.Id, Marshal(request.Olds), Marshal(request.News), TimeSpan.FromSeconds(request.Timeout), request.IgnoreChanges.ToImmutableArray(), request.Preview);
                using var cts = GetToken(context);
                var domResponse = await Implementation.Update(domRequest, cts.Token);
                var grpcResponse = new Pulumirpc.UpdateResponse();
                grpcResponse.Properties = domResponse.Properties == null ? null : PropertyValue.Unmarshal(domResponse.Properties);
                return grpcResponse;
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override async Task<Empty> Delete(Pulumirpc.DeleteRequest request, ServerCallContext context)
        {
            try
            {
                var domRequest = new DeleteRequest(request.Urn, request.Id, Marshal(request.Properties), TimeSpan.FromSeconds(request.Timeout));
                using var cts = GetToken(context);
                await Implementation.Delete(domRequest, cts.Token);
                return new Empty();
            }
            catch (NotImplementedException ex)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
            }
            catch (TaskCanceledException ex)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, ex.Message));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }
        }

        public override Task<Pulumirpc.ConstructResponse> Construct(Pulumirpc.ConstructRequest request, ServerCallContext context)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, "Component resources not yet supported"));
        }

        public override Task<Pulumirpc.CallResponse> Call(Pulumirpc.CallRequest request, ServerCallContext context)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, "Component resources not yet supported"));
        }
    }
}
