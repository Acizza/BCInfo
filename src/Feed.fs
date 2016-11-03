module Feed

open Config.Args
open System.Net
open System.Text.RegularExpressions
open Util

module Average =
    open FSharp.Data
    open System.IO

    type T = {
        Moving : int list
        Hourly : int array
    }

    let create hourlySize = {
        Moving = []
        Hourly = Array.zeroCreate hourlySize
    }

    let createWithHourlyData source = {
        Moving = []
        Hourly = source
    }

    let update hour maxMovingSize newMovingData avg =
        let hourly = avg.Hourly.[hour]
        let moving =
            avg.Moving
            |> List.setFront newMovingData maxMovingSize

        avg.Hourly.[hour] <- moving |> List.averageBy float |> int
        {avg with Moving = moving}

    let saveToFile path avgs =
        let avgArr = avgs |> Map.toArray
        let head   = Array.head avgArr |> snd

        avgArr
        |> Array.filter (fun (_, a) -> a.Hourly.Length = head.Hourly.Length)
        |> Array.map (fun (feedID, avg) ->
            let hourly = avg.Hourly |> Array.map string
            sprintf "%d,%s"
                <| feedID
                <| String.concat "," hourly
        )
        |> fun str -> File.WriteAllLines(path, str)

    let loadFromFile path =
        match Util.touchFile path with
        | 0L -> Map.empty
        | _  ->
            CsvFile.Load(path).Rows
            |> Seq.fold (fun map row ->
                let avg =
                    row.Columns.[1..]
                    |> Array.map (Convert.tryParseInt >> Option.defaultArg 0)
                    |> createWithHourlyData

                let feedID = Convert.tryParseInt row.[0]
                match feedID with
                | Some id -> Map.add id avg map
                | None    -> map
            ) Map.empty

type Feed = {
    Id           : int
    Name         : string
    Listeners    : int
    Info         : string option
    AvgListeners : Average.T
}

let createFromHTML str =
    let create (m : Match) =
        let value (i : int) = m.Groups.[i].Value
        {
            Listeners   = 1 |> value |> int
            Id          = 2 |> value |> int
            Name        = 3 |> value
            Info        = if (5 |> value).Length > 0
                          then Some <| value 5
                          else None
            AvgListeners = Average.create 24
        }

    let regex = """<td class="c m">(\d+).*?<a href="/listen/feed/(\d+)">(.+?)</a>(<br /><br /> <div class="messageBox">(.+?)</div>)?"""
    Regex.Matches(str, regex, RegexOptions.Compiled)
    |> Seq.cast<Match>
    |> Seq.map create
    |> Seq.toArray

let createFromURL (url : string) =
    use c = new WebClient()
    c.DownloadString(url)
        .Trim()
        .Replace("\n", " ")
    |> createFromHTML

/// Display a feed as a notification
let display feed index numFeeds =
    let infoStr =
        match feed.Info with
        | Some s -> sprintf "\nInfo: %s" s
        | None -> ""

    Notification.createUpdate
        index
        numFeeds
        (sprintf "Name: %s\nListeners: %d%s\nLink: https://broadcastify.com/listen/feed/%d"
            feed.Name
            feed.Listeners
            infoStr
            feed.Id)

/// Display an array of feeds as notifications
let displayAll sortOrder feeds =
    let sort =
        match sortOrder with
        | Ascending  -> Array.rev
        | Descending -> id

    feeds
    |> Array.mapi (fun i f -> (i, f))
    |> sort
    |> Array.iter (fun (i, f) -> display f (i+1) (Array.length feeds))

open Average

/// Filter out feeds that belong on any blacklists or don't spike in listeners / have a status
let filter (config : Config.Config) hour threshold feeds =
    let isPastAverage f =
        let threshold =
            config.Thresholds
            |> Seq.tryFind (fun t -> t.Name = f.Name)
            |> Option.map  (fun t -> float t.Threshold / 100. + 1.)
            |> Option.defaultArg threshold
        float f.Listeners >= float f.AvgListeners.Hourly.[hour] * threshold
    
    let isBlacklisted feed =
        let isInfoBlacklisted () =
            match feed.Info with
            | Some info ->
                let containsAnyWord =
                    Seq.exists (fun (word : string) ->
                        info.ToLower().Contains (word.ToLower())
                    )

                // Eliminate empty entries to avoid false positives
                let whitelist = config.``Info Whitelist`` |> Seq.filter ((<>) "")
                let blacklist = config.``Info Blacklist`` |> Seq.filter ((<>) "")

                if Seq.length whitelist > 0
                then whitelist |> containsAnyWord |> not // Blacklist anything not on the whitelist
                else blacklist |> containsAnyWord
            | None -> false

        Seq.contains feed.Name config.Blacklist || isInfoBlacklisted ()

    let isValid f = (isBlacklisted >> not) f && (isPastAverage f || Option.isSome f.Info)
    feeds |> Array.filter isValid