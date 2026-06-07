using grpc = global::Grpc.Core;

namespace Miningcore.Blockchain.Hoosat.Htnd;

public partial class HtndRPC
{
    public HtndRPC(string serviceName)
    {
        __Method_MessageStream = new grpc::Method<HtndMessage, HtndMessage>(
            grpc::MethodType.DuplexStreaming,
            serviceName,
            "MessageStream",
            __Marshaller_HtndMessage,
            __Marshaller_HtndMessage);
    }

    [global::System.CodeDom.Compiler.GeneratedCode("grpc_csharp_plugin", null)]
    static void __Helper_SerializeMessage(global::Google.Protobuf.IMessage message, grpc::SerializationContext context)
    {
#if !GRPC_DISABLE_PROTOBUF_BUFFER_SERIALIZATION
        if (message is global::Google.Protobuf.IBufferMessage)
        {
            context.SetPayloadLength(message.CalculateSize());
            global::Google.Protobuf.MessageExtensions.WriteTo(message, context.GetBufferWriter());
            context.Complete();
            return;
        }
#endif
        context.Complete(global::Google.Protobuf.MessageExtensions.ToByteArray(message));
    }

    [global::System.CodeDom.Compiler.GeneratedCode("grpc_csharp_plugin", null)]
    static class __Helper_MessageCache<T>
    {
        public static readonly bool IsBufferMessage = global::System.Reflection.IntrospectionExtensions.GetTypeInfo(typeof(global::Google.Protobuf.IBufferMessage)).IsAssignableFrom(typeof(T));
    }

    [global::System.CodeDom.Compiler.GeneratedCode("grpc_csharp_plugin", null)]
    static T __Helper_DeserializeMessage<T>(grpc::DeserializationContext context, global::Google.Protobuf.MessageParser<T> parser)
        where T : global::Google.Protobuf.IMessage<T>
    {
#if !GRPC_DISABLE_PROTOBUF_BUFFER_SERIALIZATION
        if (__Helper_MessageCache<T>.IsBufferMessage)
            return parser.ParseFrom(context.PayloadAsReadOnlySequence());
#endif

        return parser.ParseFrom(context.PayloadAsNewBuffer());
    }

    [global::System.CodeDom.Compiler.GeneratedCode("grpc_csharp_plugin", null)]
    static readonly grpc::Marshaller<HtndMessage> __Marshaller_HtndMessage = grpc::Marshallers.Create(__Helper_SerializeMessage,
        context => __Helper_DeserializeMessage(context, HtndMessage.Parser));

    [global::System.CodeDom.Compiler.GeneratedCode("grpc_csharp_plugin", null)]
    public grpc::Method<HtndMessage, HtndMessage> __Method_MessageStream { get; }

    public partial class HtndRPCClient : grpc::ClientBase<HtndRPCClient>
    {
        public HtndRPCClient(HtndRPC htndRpc, grpc::ChannelBase channel) : base(channel)
        {
            __HtndRPC = htndRpc;
        }

        public HtndRPCClient(HtndRPC htndRpc, grpc::CallInvoker callInvoker) : base(callInvoker)
        {
            __HtndRPC = htndRpc;
        }

        protected HtndRPCClient() : base()
        {
        }

        protected HtndRPCClient(ClientBaseConfiguration configuration) : base(configuration)
        {
        }

        protected HtndRPCClient(HtndRPC htndRpc, ClientBaseConfiguration configuration) : base(configuration)
        {
            __HtndRPC = htndRpc;
        }

        public HtndRPC __HtndRPC { get; private set; }

        [global::System.CodeDom.Compiler.GeneratedCode("grpc_csharp_plugin", null)]
        public virtual grpc::AsyncDuplexStreamingCall<HtndMessage, HtndMessage> MessageStream(grpc::Metadata headers = null,
            global::System.DateTime? deadline = null,
            global::System.Threading.CancellationToken cancellationToken = default)
        {
            return MessageStream(new grpc::CallOptions(headers, deadline, cancellationToken));
        }

        [global::System.CodeDom.Compiler.GeneratedCode("grpc_csharp_plugin", null)]
        public virtual grpc::AsyncDuplexStreamingCall<HtndMessage, HtndMessage> MessageStream(grpc::CallOptions options)
        {
            return CallInvoker.AsyncDuplexStreamingCall(__HtndRPC.__Method_MessageStream, null, options);
        }

        [global::System.CodeDom.Compiler.GeneratedCode("grpc_csharp_plugin", null)]
        protected override HtndRPCClient NewInstance(ClientBaseConfiguration configuration)
        {
            return new HtndRPCClient(__HtndRPC, configuration);
        }
    }
}