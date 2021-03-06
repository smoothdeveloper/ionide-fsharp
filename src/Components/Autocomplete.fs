[<ReflectedDefinition>]
module Atom.FSharp.AutocompleteProvider

open FunScript
open FunScript.TypeScript
open FunScript.TypeScript.fs
open FunScript.TypeScript.child_process
open FunScript.TypeScript.AtomCore
open FunScript.TypeScript.text_buffer

open Atom
open Atom.FSharp
open Atom.FSharp.DTO
open Atom.FSharp.GlyphMaps
open Atom.FSharp.CompletionHelpers
open Atom.FSharp.Control

//---------------------------------------------------------------------------------------
// Events for communication with agents
//---------------------------------------------------------------------------------------

/// A request to provide autocompletion list from the editor
type SuggestionRequest =
    { Row:int; Column:int; Line:string; Prefix:string; Path:string; Editor:IEditor }

/// Autocomplete agent handles two events - `UpdateCompletion` is a request to throw
/// away current state & update ASAP and `GetSuggestion` is request for completion data
type AutocompleteEvent =
    | GetSuggestion of SuggestionRequest * ReplyChannel<Suggestion[]>
    | UpdateCompletion

/// Helptext agent handles three events - `Show` is a request to show helptext for currently
/// selected suggestion, `Next` is request to change overload showed in tooltip,
/// and `Hide` hides current tooltip
type HelptextEvent =
    | Show of string
    | Next of int
    | Hide

let helptextEvent = new Event<HelptextEvent>()

// --------------------------------------------------------------------------------------
// Autocomplete agent that takes care of state
// --------------------------------------------------------------------------------------

let autocompleteAgent = MailboxProcessor.Start(fun inbox ->

  /// Call the F# langauge service to get autocompletion list
  let updateLastResult request = async {
      // If character before prefix is \, we provide Unicode glyph completion
      let backslashColumn = request.Column - request.Prefix.Length - 1
      if backslashColumn >= 0 && request.Line.[backslashColumn] = '\\' then
          Logger.log "AutoComplete" "updateLastResult - using unicode map"
          return unicode_map
      else
          Logger.log "Autocomplete" "updateLastResult - calling language service"
          let! _ = LanguageService.parseEditor request.Editor
          let! result = LanguageService.completion request.Path (request.Row + 1) (request.Column + 1)
          return
            match result with
            | Some r ->
                Logger.logf "Autocomplete" "updateLastResult: %O" [| r.Data |]
                r.Data
            | None -> [||] }

  /// Check if last data data is good for a given request.
  /// If no, refresh data from the F# language service
  let ensureUpToDate lastRow lastResult request = async {
      if request.Prefix = "" || request.Row <> lastRow then
          let! lastResult = updateLastResult request
          return request.Row, lastResult
      else return lastRow, lastResult }


  /// The state machine. In `updating` state, we're waiting for
  /// a request so that we can update autocomplete information ASAP.
  let rec updating () = async {
      Logger.log "Autocomplete" "updating..."
      let! msg = inbox.Receive()
      match msg with
      | UpdateCompletion -> return! updating ()
      | GetSuggestion(request, _) ->
            // Update last result and resent the completion request
            let! results = updateLastResult request
            inbox.Post(msg)
            return! usingLastResult request.Row results }

  /// In `usingLastResult` state, we have results from previous completion
  /// and we keep using it until we get `UpdateCompletion`
  and usingLastResult lastRow (lastResult:Completion[]) : Async<unit> = async {
      Logger.logf "Autocomplete" "Using last result: %O" [| lastResult |]
      let! evt = inbox.Receive()
      match evt with
      | UpdateCompletion -> return! updating ()
      | GetSuggestion(request, reply) ->
            // Check that last result is good (if not, get a new one)
            let! lastRow, lastResult = ensureUpToDate lastRow lastResult request
            Logger.logf "Autocomplete" "Handling request: %O" [| request |]
            let r =
                if (request.Column > 0 && request.Line.[request.Column - 1] = '\\') then
                    glyph_completion request.Prefix lastResult
                else
                    fsharp_completion request.Prefix lastResult
            if r.Length > 0 then helptextEvent.Trigger (Show r.[0].text)
            reply.Reply(r)
            return! usingLastResult lastRow lastResult }

  // Initially, we have no data, so update ASAP.
  updating() )


// --------------------------------------------------------------------------------------
// Help Text (the tooltips that pop up to the side of the autocomplete window)
// --------------------------------------------------------------------------------------

/// Creates a tool tip and start an async loop to switch between the given overloads.
/// When `helptextEvent` happens with `Hide` or `Show` (indicating that we are showing
/// another tool tip), the workflow stops & tool tip disappears.
let createHelptextToolTip (overloads:DTO.OverloadSignature[]) (position:JQueryCoordinates) =

    // Create tool tip for showing help text & add it to the right place
    let helptext =
        jq("<div class='type-tooltip tooltip'>
              <div class='tooltip-inner'>TEST</div>
            </div>").appendTo(jq(".panes")).offset(position)


    // Update the UI & wait for `Move` event (to change overload) or `Hide` to remove tooltip
    let rec loop i = async {

        let toolTipInner = jq' helptext.[0].firstElementChild
        toolTipInner.empty() |> ignore

        let text = overloads.[i].Signature
        if overloads.Length > 1 then
          sprintf "<div class='tooltip-counter'>%d of %d</div>" (i + 1) overloads.Length
          |> toolTipInner.append |> ignore

        jq("<div/>").text(text).html().Replace("\\n", "<br />").Replace("\n", "<br />")
        |> toolTipInner.append |> ignore

        let! evt = Async.AwaitObservable helptextEvent.Publish
        match evt with
        | Next by -> return! loop ((i + by + overloads.Length) % overloads.Length)
        | Show _ | Hide -> helptext.fadeOut().remove() |> ignore }

    // Start by displaying the first overload
    loop 0


/// When `helptextEvent` happens with `Show`, this calls `createHelptextToolTip` to create 
/// tool tip user interface and start a handler for a new tool tip
let handleShowHelptextRequests () = async {
    while true do
      // Wait for the next tool tip request & get data from FsAutoComplete
      let! text = 
        helptextEvent.Publish 
        |> Observable.choose (function Show s -> Some s | _ -> None)
        |> Async.AwaitObservable
      let! tip = LanguageService.helptext text
      
      // Calculate coordinates for the tool tip & start it
      match tip with
      | None -> ()
      | Some n ->
          // The suggestion list might not be visible yet, so we wait up to 500ms 
          // before we give up (as we do not know location for the tool tip) (fix #199)
          let rec getSuggestionListOffset n = async {
              let li = (jq ".suggestion-list-scroller .list-group li.selected")
              let o = li.offset()
              if JS.isDefined o && li.length > 0. then return Some(o, li)
              elif n = 0 then return None
              else 
                  do! Async.Sleep 50
                  return! getSuggestionListOffset (n - 1) }

          let! offs = getSuggestionListOffset 10
          match offs with 
          | None -> () 
          | Some (o, li) ->
              let list = jq "autocomplete-suggestion-list"
              o.left <- o.left + list.width() + 10.
              o.top <- o.top - li.height() - 10.
              let helptextList = n.Data.Overloads |> Array.concat
              if not (Array.isEmpty helptextList) then
                  createHelptextToolTip helptextList o |> Async.StartImmediate }

// --------------------------------------------------------------------------------------
// Editor integration
// --------------------------------------------------------------------------------------

/// Triggered to ensure that event handlers for autocomplete-plus are registered
let checkAutoCompleteManager = Event<unit>()

/// State-less getSuggestion function that is called by Atom
let getSuggestion (options:GetSuggestionOptions) = Async.StartAsPromise <| async {
    if unbox<obj>(options.editor.buffer.file) = null then
        return [| |]
    else
        // Check to make sure autocompletemanager events are handled
        checkAutoCompleteManager.Trigger ()
        // Get information for the autocomplete request
        let path = options.editor.buffer.file.path
        let row = int options.bufferPosition.row
        let col = int options.bufferPosition.column
        let line = options.editor.buffer.lines.[row]
        let prefix = match options.prefix with "." | "=" -> "" | _ -> options.prefix

        // Prefix is a number and we do not want to provide autocomplete on floats, e.g. `132.`
        if prefix <> "" && not (Globals.isNaN(unbox prefix)) then
            return [| |]
        else
            let request =
              { Row = row; Column = col; Prefix = prefix; Line = line ;
                Path = path; Editor = options.editor }
            return! autocompleteAgent.PostAndAsyncReply(fun reply ->
                GetSuggestion(request, reply) ) }

/// Triggered when a current editor is changed
let editorChanged = Event<IEditor>()

/// Register a handler that hides the 'helptext' whenever cursor position in the current F# editor changes
let rec registerHelptextHandler disposePrevious = async {
    let! editor = Async.AwaitObservable editorChanged.Publish
    disposePrevious ()
    let dispose =
        if not (isFSharpEditor editor) then ignore
        else editor.onDidChangeCursorPosition((fun _ ->
            helptextEvent.Trigger Hide ) |> unbox<Function>).dispose
    return! registerHelptextHandler dispose }

/// Triggered when the `autocompleteManager` from `autocomplete-plus` is available
let autoCompleteManagerAvailable =
    checkAutoCompleteManager.Publish |> Observable.choose (fun () ->
        try let package = Globals.atom.packages.getLoadedPackage("autocomplete-plus") |> unbox<Package>
            Some(package.mainModule.autocompleteManager.suggestionList.emitter)
        with _ -> None)

/// Register a handler for events triggered by scrolling through autocomplete-plus completion list
let registerCompletionScrollHandlers () = async {
    let! emitter = autoCompleteManagerAvailable |> Async.AwaitObservable
    let handler flag =
        let selected =
            if flag then (jq "li.selected").prev().find("span.word-container .word")
            else (jq "li.selected").next().find("span.word-container .word")
        let text =
            if selected.length > 0. then selected.text()
            elif flag then
                (jq ".suggestion-list-scroller .list-group li").last().find(" span.word-container .word").text()
            else
                (jq ".suggestion-list-scroller .list-group li").first().find(" span.word-container .word").text()
        helptextEvent.Trigger (Show text)
    emitter.add("did-select-next", fun _ -> handler false)
    emitter.add("did-select-previous", fun _ -> handler true)
    emitter.add("did-cancel", fun _ -> helptextEvent.Trigger Hide) }


/// Initialize the autocomplete package
let create () =
    // Handler for Ctrl+Space command
    Globals.atom.commands.add("atom-text-editor","fsharp:autocomplete", fun _ ->
        dispatchAutocompleteCommand ()
        autocompleteAgent.Post(AutocompleteEvent.UpdateCompletion) )

    // Update tool tip when another item in the completion list is chosen
    registerCompletionScrollHandlers () |> Async.StartImmediate
    checkAutoCompleteManager.Trigger ()

    // Start handler for showing tool tip on the side of completion lists
    handleShowHelptextRequests () |> Async.StartImmediate

    // Go to the next/previous overload in the help tool tip
    Globals.atom.commands.add("atom-text-editor","fsharp:helptext-next", fun _ ->
        helptextEvent.Trigger (Next +1))
    Globals.atom.commands.add("atom-text-editor","fsharp:helptext-previous", fun _ ->
        helptextEvent.Trigger (Next -1))

    // Hide tool tip on the side of completion list when cursor position changes
    registerHelptextHandler ignore |> Async.StartImmediate
    Globals.atom.workspace.getActiveTextEditor() |> editorChanged.Trigger
    Globals.atom.workspace.onDidChangeActivePaneItem((fun ed ->
        editorChanged.Trigger ed) |> unbox<Function>  ) |> ignore

    // Provider for Unicode  Glyph Autocompletion Service
    {   selector = ".source.fsharp"
        //disableForSelector = ".source.fsharp .string, .source.fsharp .comment"
        disableForSelector = " "
        inclusionPriority = 1
        excludeLowerPriority = true
        getSuggestions = getSuggestion
    }
