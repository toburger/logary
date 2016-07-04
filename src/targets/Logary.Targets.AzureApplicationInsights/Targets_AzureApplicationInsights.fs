module Logary.Targets.AzureApplicationInsights

open Hopac
open Hopac.Infixes
open Logary
open Logary.Target
open Logary.Internals

open Microsoft.ApplicationInsights
open Microsoft.ApplicationInsights.Extensibility

type AzureApplicationInsightsConf =
  { /// Specify the instrumentation key.
    instrumentationKey : string
    logMetrics : bool }
    
/// Empty Azure Application Insights configuration
let empty =
  { instrumentationKey = ""
    logMetrics = false }

module internal Impl =

  type State =
    { client : TelemetryClient }

  let createState instrumentationKey : State =
    let client = new TelemetryClient(TelemetryConfiguration.CreateDefault(InstrumentationKey = instrumentationKey))
    { client = client }

  let loop (conf : AzureApplicationInsightsConf)
           (ri : RuntimeInfo)
           (requests : RingBuffer<_>)
           (shutdown : Ch<_>) =

    let rec init config =
      createState config.instrumentationKey |> loop

    and loop (state : State) : Job<unit> =
      Alt.choose [
        shutdown ^=> fun ack -> job {
          do! ack *<= ()
        }

        RingBuffer.take requests ^=> function
          | Log (message, ack) ->
            job {
              do! Job.Scheduler.isolate (fun _ ->
                  let telemetry = DataContracts.EventTelemetry(message.name.ToString())
                  telemetry.Context.Operation.Id <- System.Guid.NewGuid().ToString()
                  telemetry.Timestamp <- NodaTime.Instant.FromTicksSinceUnixEpoch(message.timestampTicks).ToDateTimeOffset()
                  state.client.TrackEvent(telemetry))

              do! ack *<= ()
              return! loop state
            }

          | Flush (ackCh, nack) ->
            job {
              do! Ch.give ackCh () <|> nack
              return! loop state
            }
      ] :> Job<_>

    init conf

let create conf = TargetUtils.stdNamedTarget (Impl.loop conf)

/// Use with LogaryFactory.New( s => s.Target<Logstash.Builder>() )
type Builder(conf, callParent : FactoryApi.ParentCallback<Builder>) =

  member x.PublishTo(instrumentationKey : string) =
    Builder({ conf with instrumentationKey = instrumentationKey }, callParent)

  member x.LogMetrics() =
    Builder({ conf with logMetrics = true }, callParent)

  member x.Done() =
    ! (callParent x)

  new(callParent : FactoryApi.ParentCallback<_>) =
    Builder(empty, callParent)

  interface Logary.Target.FactoryApi.SpecificTargetConf with
    member x.Build name =
      create conf name