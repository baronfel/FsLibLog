namespace FsLibLog

module Types =
    open System
    type LogLevel =
    | Trace = 0
    | Debug = 1
    | Info = 2
    | Warn = 3
    | Error = 4
    | Fatal = 5

    /// An optional message thunk.
    ///
    /// - If `None` is provided, this typically signals to the logger to do a isEnabled check.
    /// - If `Some` is provided, this signals the logger to log.
    type MessageThunk = (unit -> string) option

    /// The signature of a log message function
    type Logger = LogLevel -> MessageThunk -> exn option -> obj array -> bool

    /// An interface wrapper for `Logger`. Useful when using depedency injection frameworks.
    type ILog =
        abstract member Log :  Logger

    /// An interface for retrieving a concrete logger such as Serilog, Nlog, etc.
    type ILogProvider =
        abstract member GetLogger : string -> Logger
        abstract member OpenNestedContext : string -> IDisposable
        abstract member OpenMappedContext : string -> obj -> bool -> IDisposable


module Providers =
    module ConsoleProvider =
        open System
        open System.Globalization
        open Types

        let isAvailable () = true

        type private ConsoleProvider () =
            let threadSafeWriter =  MailboxProcessor.Start(fun inbox ->
                let rec loop () = async {
                    let! (consoleColor, message : string) = inbox.Receive()
                    let originalForground = Console.ForegroundColor
                    try
                        Console.ForegroundColor <- consoleColor
                        do! Console.Out.WriteLineAsync(message) |> Async.AwaitTask
                    finally
                        Console.ForegroundColor <- originalForground
                    return! loop ()
                }
                loop ()
            )
            let levelToColor =
                Map([
                    (LogLevel.Fatal, ConsoleColor.DarkRed)
                    (LogLevel.Error, ConsoleColor.Red)
                    (LogLevel.Warn, ConsoleColor.Yellow)
                    (LogLevel.Info, ConsoleColor.White)
                    (LogLevel.Debug, ConsoleColor.Gray)
                    (LogLevel.Trace, ConsoleColor.DarkGray)
                ])
            let writeMessage name logLevel (messageFunc : MessageThunk) ``exception`` formatParams =
                match messageFunc with
                | None -> true
                | Some m ->
                    let color =
                        match levelToColor |> Map.tryFind(logLevel) with
                        | Some color -> color
                        | None -> Console.ForegroundColor
                    let formattedMsg =
                        let msg = String.Format(CultureInfo.InvariantCulture, m (), formatParams)
                        let msg =
                            match ``exception`` with
                            | Some (e : exn) ->
                                String.Format("{0} | {1}", msg, e.ToString())
                            | None ->
                                msg
                        String.Format("{0} | {1} | {2} | {3}", DateTime.UtcNow, logLevel, name, msg)

                    threadSafeWriter.Post(color, formattedMsg)
                    true

            interface ILogProvider with

                member this.GetLogger(name: string): Logger =
                    writeMessage name
                member this.OpenMappedContext(arg1: string) (arg2: obj) (arg3: bool): System.IDisposable =
                    failwith "Not Implemented"
                member this.OpenNestedContext(arg1: string): System.IDisposable =
                    failwith "Not Implemented"

        let create () =
            ConsoleProvider () :> ILogProvider

    module SerilogProvider =
        open System
        open Types
        open System.Linq.Expressions

        let getLogManagerType () =
            Type.GetType("Serilog.Log, Serilog")
        let isAvailable () =
            getLogManagerType () |> isNull |> not

        let getForContextMethodCall () =
            let logManagerType = getLogManagerType ()
            let method = logManagerType.GetMethod("ForContext", [|typedefof<string>; typedefof<obj>; typedefof<bool>|])
            let propertyNameParam = Expression.Parameter(typedefof<string>, "propertyName")
            let valueParam = Expression.Parameter(typedefof<obj>, "value")
            let destructureObjectsParam = Expression.Parameter(typedefof<bool>, "destructureObjects")
            let exrs : Expression []=
                [|
                    propertyNameParam
                    valueParam
                    destructureObjectsParam
                |]
            let methodCall =
                Expression.Call(null, method, exrs)
            let func =
                Expression.Lambda<Func<string, obj, bool, obj>>(
                    methodCall,
                    propertyNameParam,
                    valueParam,
                    destructureObjectsParam).Compile()
            fun name -> func.Invoke("SourceContext", name, false)

        type SerilogGateway = {
            Write : obj -> obj -> string -> obj [] -> unit
            WriteException : obj -> obj -> exn -> string -> obj [] -> unit
            IsEnabled : obj -> obj -> bool
            TranslateLevel : LogLevel -> obj
        } with
            static member Create () =
                let logEventLevelType = Type.GetType("Serilog.Events.LogEventLevel, Serilog")
                if (logEventLevelType |> isNull) then
                    failwith ("Type Serilog.Events.LogEventLevel was not found.")

                let debugLevel = Enum.Parse(logEventLevelType, "Debug", false)
                let errorLevel = Enum.Parse(logEventLevelType, "Error", false)
                let fatalLevel = Enum.Parse(logEventLevelType, "Fatal", false)
                let informationLevel = Enum.Parse(logEventLevelType, "Information", false)
                let verboseLevel = Enum.Parse(logEventLevelType, "Verbose", false)
                let warningLevel = Enum.Parse(logEventLevelType, "Warning", false)
                let translateLevel (level : LogLevel) =
                    match level with
                    | LogLevel.Fatal -> fatalLevel
                    | LogLevel.Error -> errorLevel
                    | LogLevel.Warn -> warningLevel
                    | LogLevel.Info -> informationLevel
                    | LogLevel.Debug -> debugLevel
                    | LogLevel.Trace -> verboseLevel
                    | _ -> debugLevel

                let loggerType = Type.GetType("Serilog.ILogger, Serilog")
                if (loggerType |> isNull) then failwith ("Type Serilog.ILogger was not found.")
                let isEnabledMethodInfo = loggerType.GetMethod("IsEnabled", [|logEventLevelType|])
                let instanceParam = Expression.Parameter(typedefof<obj>)
                let instanceCast = Expression.Convert(instanceParam, loggerType)
                let levelParam = Expression.Parameter(typedefof<obj>)
                let levelCast = Expression.Convert(levelParam, logEventLevelType)
                let isEnabledMethodCall = Expression.Call(instanceCast, isEnabledMethodInfo, levelCast)
                let isEnabled =
                    Expression
                        .Lambda<Func<obj, obj, bool>>(isEnabledMethodCall, instanceParam, levelParam).Compile()

                let writeMethodInfo =
                    loggerType.GetMethod("Write", [|logEventLevelType; typedefof<string>; typedefof<obj []>|])
                let messageParam = Expression.Parameter(typedefof<string>)
                let propertyValuesParam = Expression.Parameter(typedefof<obj []>)
                let writeMethodExp =
                    Expression.Call(
                        instanceCast,
                        writeMethodInfo,
                        levelCast,
                        messageParam,
                        propertyValuesParam)
                let expression =
                    Expression.Lambda<Action<obj, obj, string, obj []>>(
                        writeMethodExp,
                        instanceParam,
                        levelParam,
                        messageParam,
                        propertyValuesParam)
                let write = expression.Compile()

                // // Action<object, object, string, Exception> WriteException =
                // // (logger, level, exception, message) => { ((ILogger)logger).Write(level, exception, message, new object[]); }
                let writeExceptionMethodInfo =
                    loggerType.GetMethod(
                        "Write",
                        [| logEventLevelType; typedefof<exn>; typedefof<string>; typedefof<obj []>|])
                let exceptionParam = Expression.Parameter(typedefof<exn>)
                let writeMethodExp =
                    Expression.Call(
                        instanceCast,
                        writeExceptionMethodInfo,
                        levelCast,
                        exceptionParam,
                        messageParam,
                        propertyValuesParam)
                let writeException =
                    Expression.Lambda<Action<obj, obj, exn, string, obj []>>(
                        writeMethodExp,
                        instanceParam,
                        levelParam,
                        exceptionParam,
                        messageParam,
                        propertyValuesParam).Compile()
                {
                    Write = (fun logger level message formattedParmeters -> write.Invoke(logger,level,message,formattedParmeters))
                    WriteException = fun logger level ex message formattedParmeters -> writeException.Invoke(logger,level,ex,message,formattedParmeters)
                    IsEnabled = fun logger level -> isEnabled.Invoke(logger,level)
                    TranslateLevel = translateLevel
                }

        type private SerigLogProvider () =
            let getLoggerByName = getForContextMethodCall ()
            let serilogGatewayInit = lazy(SerilogGateway.Create())

            let writeMessage logger logLevel (messageFunc : MessageThunk) ``exception`` formatParams =
                let serilogGateway = serilogGatewayInit.Value
                let translatedValue = serilogGateway.TranslateLevel logLevel
                match messageFunc with
                | None -> serilogGateway.IsEnabled logger translatedValue
                | Some _ when  serilogGateway.IsEnabled logger translatedValue |> not -> false
                | Some m ->
                    match ``exception`` with
                    | Some ex ->
                        serilogGateway.WriteException logger translatedValue ex (m()) formatParams
                    | None ->
                        serilogGateway.Write logger translatedValue (m()) formatParams
                    true

            interface ILogProvider with
                member this.GetLogger(name: string): Logger =
                    let logger =  getLoggerByName (name)
                    printfn "%A" logger
                    writeMessage logger
                member this.OpenMappedContext(arg1: string) (arg2: obj) (arg3: bool): IDisposable =
                    failwith "Not Implemented"
                member this.OpenNestedContext(arg1: string): IDisposable =
                    failwith "Not Implemented"

        let create () =
            SerigLogProvider () :> ILogProvider



module LogProvider =
    open System
    open Types
    open Providers
    open System.Diagnostics

    let mutable private currentLogProvider = None

    let private knownProviders = [
        (SerilogProvider.isAvailable , SerilogProvider.create)
        // (ConsoleProvider.isAvailable, ConsoleProvider.create)
    ]

    /// Greedy search for first available LogProvider. Order of known providers matters.
    let private resolvedLogger = lazy (
        knownProviders
        |> Seq.tryFind(fun (isAvailable,_) -> isAvailable ())
        |> Option.map(fun (_, create) -> create())
    )

    let  inline noopLogger _ _ _ _ = false

    /// **Description**
    ///
    /// Allows custom override when `getLogger` searches for a LogProvider.
    ///
    /// **Parameters**
    ///   * `provider` - parameter of type `ILogProvider`
    ///
    /// **Output Type**
    ///   * `unit`
    let setLoggerProvider (logProvider : ILogProvider) =
        currentLogProvider <- Some logProvider

    /// **Description**
    ///
    /// Creates a logger given a `Type`.  This will attempt to retrieve any loggers set with `setLoggerProvider`.  It will fallback to a known list of providers.
    ///
    /// **Parameters**
    ///   * `type` - parameter of type `Type`
    ///
    /// **Output Type**
    ///   * `ILog`
    let getLogger (``type`` : Type) =
        let loggerProvider =
            match currentLogProvider with
            | None -> resolvedLogger.Value
            | Some p -> Some p
        let logFunc =
            match loggerProvider with
            | Some loggerProvider -> loggerProvider.GetLogger(``type``.ToString())
            | None -> noopLogger
        { new ILog with member x.Log = logFunc}

    /// **Description**
    ///
    /// Creates a logger. It's name is based on the current StackFrame.
    ///
    /// **Output Type**
    ///   * `ILog`
    ///
    let getCurrentLogger ()   =
        let stackFrame = StackFrame(2, false)
        getLogger(stackFrame.GetMethod().DeclaringType)