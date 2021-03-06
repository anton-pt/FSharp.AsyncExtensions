﻿// ----------------------------------------------------------------------------
// F# async extensions (Observable.fs)
// (c) Tomas Petricek, 2011, Available under Apache 2.0 license.
// ----------------------------------------------------------------------------
#nowarn "40"
namespace FSharp.Control

open System
open System.Threading

// ----------------------------------------------------------------------------

/// Union type that represents different messages that can be sent to the
/// IObserver interface. The IObserver type is equivalent to a type that has
/// just OnNext method that gets 'ObservableUpdate' as an argument.
type ObservableUpdate<'T> = 
  | Next of 'T
  | Error of exn
  | Completed

module Observable =

  /// Returns an observable that yields sliding windows of 
  /// containing elements drawn from the input observable. 
  /// Each window is returned as a fresh array.
  let windowed size (input:IObservable<'T>) =
    { new IObservable<'T[]> with
        member x.Subscribe(observer) =
          // Create sliding window agent for every call
          // and redirect batches to the observer
          let cts = new CancellationTokenSource()
          let agent = new SlidingWindowAgent<_>(size, cts.Token)
          agent.WindowProduced.Add(observer.OnNext)

          // Subscribe to the input and send values to the agent
          let subscription = 
            input.Subscribe
              ({ new IObserver<'T> with
                  member x.OnNext(v) = agent.Enqueue(v)
                  member x.OnCompleted() = 
                    cts.Cancel()
                    observer.OnCompleted()
                  member x.OnError(e) = 
                    cts.Cancel()
                    observer.OnError(e) })

          // Cancel subscription & cancel the agent
          { new IDisposable with 
              member x.Dispose() =
                subscription.Dispose()
                cts.Cancel() } }

  /// Creates an observable that calls the specified function (each time)
  /// after an observer is attached to the observable. This is useful to 
  /// make sure that events triggered by the function are handled. 
  let guard f (e:IObservable<'Args>) =  
    { new IObservable<'Args> with  
        member x.Subscribe(observer) =  
          let rm = e.Subscribe(observer) in f(); rm } 

  /// Turns observable into an observable that only calls OnNext method of the
  /// observer, but gives it a discriminated union that represents different
  /// kinds of events (error, next, completed)
  let asUpdates (input:IObservable<'T>) = 
    { new IObservable<_> with
        member x.Subscribe(observer) =
          input.Subscribe
            ({ new IObserver<_> with
                member x.OnNext(v) = observer.OnNext(Next v)
                member x.OnCompleted() = observer.OnNext(Completed) 
                member x.OnError(e) = observer.OnNext(Error e) }) }

// ----------------------------------------------------------------------------

[<AutoOpen>]
module ObservableExtensions =

  /// Helper that can be used for writing CPS-style code that resumes
  /// on the same thread where the operation was started.
  let internal synchronize f = 
    let ctx = System.Threading.SynchronizationContext.Current 
    f (fun g ->
      let nctx = System.Threading.SynchronizationContext.Current 
      if ctx <> null && ctx <> nctx then ctx.Post((fun _ -> g()), null)
      else g() )

  type Microsoft.FSharp.Control.Async with 

    /// Behaves like AwaitObservable, but calls the specified guarding function
    /// after a subscriber is registered with the observable.
    static member GuardedAwaitObservable (ev1:IObservable<'T1>) guardFunction =
      async {
        let! token = Async.CancellationToken // capture the current cancellation token
        return! Async.FromContinuations(fun (cont, econt, ccont) ->
          // start a new mailbox processor which will await the result
          Agent.Start((fun (mailbox : Agent<Choice<'T1, exn, OperationCanceledException>>) ->
            async {
              // register a callback with the cancellation token which posts a cancellation message
              #if NET40
              use __ = token.Register((fun _ ->
                  mailbox.Post (Choice3Of3 (new OperationCanceledException "The opeartion was cancelled."))))
              #else
              use __ = token.Register((fun _ ->
                  mailbox.Post (Choice3Of3 (new OperationCanceledException "The opeartion was cancelled."))), null)
              #endif
          
              // subscribe to the observable: if an error occurs post an error message and post the result otherwise
              use __ = 
                ev1.Subscribe({ new IObserver<'T1> with
                    member __.OnNext result = mailbox.Post (Choice1Of3 result)
                    member __.OnError exn = mailbox.Post (Choice2Of3 exn)
                    member __.OnCompleted () =
                        let msg = "Cancelling the workflow, because the Observable awaited using AwaitObservable has completed."
                        mailbox.Post (Choice3Of3 (new OperationCanceledException(msg))) })
              
              guardFunction() // call the guard function

              // wait for the first of these messages and call the appropriate continuation function
              let! message = mailbox.Receive()
              match message with
              | Choice1Of3 reply -> cont reply
              | Choice2Of3 exn -> econt exn
              | Choice3Of3 exn -> ccont exn })) |> ignore) }

    /// Creates an asynchronous workflow that will be resumed when the 
    /// specified observables produces a value. The workflow will return 
    /// the value produced by the observable.
    static member AwaitObservable(ev1:IObservable<'T1>) =
      Async.GuardedAwaitObservable ev1 ignore
  
    /// Creates an asynchronous workflow that will be resumed when the 
    /// first of the specified two observables produces a value. The 
    /// workflow will return a Choice value that can be used to identify
    /// the observable that produced the value.
    static member AwaitObservable(ev1:IObservable<'T1>, ev2:IObservable<'T2>) = 
      List.reduce Observable.merge 
        [ ev1 |> Observable.map Choice1Of2 
          ev2 |> Observable.map Choice2Of2 ] 
      |> Async.AwaitObservable

    /// Creates an asynchronous workflow that will be resumed when the 
    /// first of the specified three observables produces a value. The 
    /// workflow will return a Choice value that can be used to identify
    /// the observable that produced the value.
    static member AwaitObservable
        ( ev1:IObservable<'T1>, ev2:IObservable<'T2>, ev3:IObservable<'T3> ) = 
      List.reduce Observable.merge 
        [ ev1 |> Observable.map Choice1Of3 
          ev2 |> Observable.map Choice2Of3
          ev3 |> Observable.map Choice3Of3 ] 
      |> Async.AwaitObservable

    /// Creates an asynchronous workflow that will be resumed when the 
    /// first of the specified four observables produces a value. The 
    /// workflow will return a Choice value that can be used to identify
    /// the observable that produced the value.
    static member AwaitObservable( ev1:IObservable<'T1>, ev2:IObservable<'T2>, 
                                   ev3:IObservable<'T3>, ev4:IObservable<'T4> ) = 
      List.reduce Observable.merge 
        [ ev1 |> Observable.map Choice1Of4 
          ev2 |> Observable.map Choice2Of4
          ev3 |> Observable.map Choice3Of4
          ev4 |> Observable.map Choice4Of4 ] 
      |> Async.AwaitObservable
