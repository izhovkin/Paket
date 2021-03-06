﻿module Paket.DependencyChangeDetection

open Paket.Domain
open Paket.Requirements
open Paket.PackageResolver
open Logging

let findNuGetChangesInDependenciesFile(dependenciesFile:DependenciesFile,lockFile:LockFile,strict) =
    let allTransitives groupName = lockFile.GetTransitiveDependencies groupName
    let hasChanged groupName transitives (newRequirement:PackageRequirement) (originalPackage:ResolvedPackage) =
        let settingsChanged() =
            if newRequirement.Settings <> originalPackage.Settings then
                if newRequirement.Settings.FrameworkRestrictions <> originalPackage.Settings.FrameworkRestrictions then
                    transitives |> Seq.contains originalPackage.Name |> not
                else true
            else false

        let requirementOk =
            if strict then
                newRequirement.VersionRequirement.IsInRange originalPackage.Version
            else
                newRequirement.IncludingPrereleases().VersionRequirement.IsInRange originalPackage.Version

        (not requirementOk) || settingsChanged()

    let added groupName transitives =
        match dependenciesFile.Groups |> Map.tryFind groupName with
        | None -> Set.empty
        | Some group ->
            let lockFileGroup = lockFile.Groups |> Map.tryFind groupName 
            group.Packages
            |> Seq.map (fun d ->
                d.Name, { d with Settings = group.Options.Settings + d.Settings })
            |> Seq.filter (fun (name,dependenciesFilePackage) ->
                match lockFileGroup with
                | None -> true
                | Some group ->
                    match group.Resolution.TryFind name with
                    | Some lockFilePackage ->
                        let p' = { lockFilePackage with Settings = group.Options.Settings + lockFilePackage.Settings }
                        hasChanged groupName transitives dependenciesFilePackage p'
                    | _ -> true)
            |> Seq.map (fun (p,_) -> groupName,p)
            |> Set.ofSeq
    
    let modified groupName transitives = 
        let directMap =
            match dependenciesFile.Groups |> Map.tryFind groupName with
            | None -> Map.empty
            | Some group ->
                group.Packages
                |> Seq.map (fun d -> d.Name,{ d with Settings = group.Options.Settings + d.Settings })
                |> Map.ofSeq

        [for t in lockFile.GetTopLevelDependencies(groupName) do            
            let name = t.Key
            match directMap.TryFind name with
            | Some pr ->
                let t = t.Value
                let t = { t with Settings = lockFile.GetGroup(groupName).Options.Settings + t.Settings }
                if hasChanged groupName transitives pr t then 
                    yield groupName, name // Modified
            | _ -> yield groupName, name // Removed
        ]
        |> List.map (fun (g,p) -> lockFile.GetAllNormalizedDependenciesOf(g,p,lockFile.FileName))
        |> Seq.concat
        |> Set.ofSeq

    let groupNames =
        dependenciesFile.Groups
        |> Seq.map (fun kv -> kv.Key)
        |> Seq.append (lockFile.Groups |> Seq.map (fun kv -> kv.Key))

    groupNames
    |> Seq.map (fun groupName -> 
            let transitives = allTransitives groupName
            let added = added groupName transitives
            let modified = modified groupName transitives
            Set.union added modified)
    |> Seq.concat
    |> Set.ofSeq

[<CustomEquality;CustomComparison>]
type RemoteFileChange =
    { Owner : string
      Project : string
      Name : string
      Origin : ModuleResolver.Origin
      Commit : string option
      AuthKey : string option }

    override this.Equals(that) = 
        match that with
        | :? RemoteFileChange as that -> 
            this.FieldsWithoutCommit = that.FieldsWithoutCommit &&
             ((this.Commit = that.Commit) || this.Commit = None || that.Commit = None)
        | _ -> false

    override this.ToString() = sprintf "%O/%s/%s" this.Origin this.Project this.Name

    member private this.FieldsWithoutCommit = this.Owner,this.Name,this.AuthKey,this.Project,this.Origin
    member private this.FieldsWithCommit = this.FieldsWithoutCommit,this.Commit
    override this.GetHashCode() = hash this.FieldsWithCommit

    static member Compare(x:RemoteFileChange,y:RemoteFileChange) =
        if x = y then 0 else
        compare x.FieldsWithCommit y.FieldsWithCommit

    interface System.IComparable with
       member this.CompareTo that = 
          match that with 
          | :? RemoteFileChange as that -> RemoteFileChange.Compare(this,that)
          | _ -> invalidArg "that" "cannot compare value of different types"

    static member CreateUnresolvedVersion (unresolved:ModuleResolver.UnresolvedSource) : RemoteFileChange =
        { Owner = unresolved.Owner
          Project = unresolved.Project
          Name = unresolved.Name.TrimStart('/')
          Origin = unresolved.Origin
          Commit = 
            match unresolved.Version with
            | ModuleResolver.VersionRestriction.NoVersionRestriction -> None
            | ModuleResolver.VersionRestriction.Concrete x -> Some x
            | ModuleResolver.VersionRestriction.VersionRequirement vr -> Some(vr.ToString())

          AuthKey = unresolved.AuthKey }

    static member CreateResolvedVersion (resolved:ModuleResolver.ResolvedSourceFile) : RemoteFileChange =
        { Owner = resolved.Owner
          Project = resolved.Project
          Name = resolved.Name
          Origin = resolved.Origin
          Commit = Some resolved.Commit
          AuthKey = resolved.AuthKey }


let findRemoteFileChangesInDependenciesFile(dependenciesFile:DependenciesFile,lockFile:LockFile) =
    let groupNames =
        dependenciesFile.Groups
        |> Seq.map (fun kv -> kv.Key)
        |> Seq.append (lockFile.Groups |> Seq.map (fun kv -> kv.Key))

    let computeDifference (lockFileGroup:LockFileGroup) (dependenciesFileGroup:DependenciesGroup) =
        let dependenciesFileRemoteFiles =
            dependenciesFileGroup.RemoteFiles
            |> List.map RemoteFileChange.CreateUnresolvedVersion
            |> Set.ofList

        let lockFileRemoteFiles =
            lockFileGroup.RemoteFiles
            |> List.map RemoteFileChange.CreateResolvedVersion
            |> List.map (fun r ->
                match dependenciesFileRemoteFiles |> Seq.tryFind (fun d -> d.Name = r.Name) with
                | Some d -> { r with Commit = d.Commit }
                | _ -> { r with Commit = None })
            |> Set.ofList

        let missingRemotes = Set.difference dependenciesFileRemoteFiles lockFileRemoteFiles
        missingRemotes

    groupNames
    |> Seq.map (fun groupName ->
            match dependenciesFile.Groups |> Map.tryFind groupName with
            | Some dependenciesFileGroup ->
                match lockFile.Groups |> Map.tryFind groupName with
                | Some lockFileGroup -> computeDifference lockFileGroup dependenciesFileGroup
                | None -> 
                    // all added
                    dependenciesFileGroup.RemoteFiles
                    |> List.map RemoteFileChange.CreateUnresolvedVersion 
                    |> Set.ofList 
            | None -> 
                // all removed
                lockFile.GetGroup(groupName).RemoteFiles
                |> List.map RemoteFileChange.CreateResolvedVersion
                |> Set.ofList
            |> Set.map (fun x -> groupName,x))
    |> Seq.concat
    |> Set.ofSeq

let GetPreferredNuGetVersions (dependenciesFile:DependenciesFile,lockFile:LockFile) =
    lockFile.GetGroupedResolution()
    |> Seq.map (fun kv ->
        let lockFileSource = kv.Value.Source
        match dependenciesFile.Groups |> Map.tryFind (fst kv.Key) with
        | None -> kv.Key, (kv.Value.Version, lockFileSource)
        | Some group -> 
            match group.Sources |> List.tryFind (fun s -> s.Url = lockFileSource.Url) with
            | Some s -> kv.Key, (kv.Value.Version, s)
            | None -> kv.Key, (kv.Value.Version, kv.Value.Source))
    |> Map.ofSeq

let GetChanges(dependenciesFile,lockFile,strict) =
    let nuGetChanges = findNuGetChangesInDependenciesFile(dependenciesFile,lockFile,strict)
    let nuGetChangesPerGroup =
        nuGetChanges
        |> Seq.groupBy fst
        |> Map.ofSeq

    let remoteFileChanges = findRemoteFileChangesInDependenciesFile(dependenciesFile,lockFile)
    let remoteFileChangesPerGroup =
        remoteFileChanges
        |> Seq.groupBy fst
        |> Map.ofSeq

    let hasNuGetChanges groupName =
        match nuGetChangesPerGroup |> Map.tryFind groupName with
        | None -> false
        | Some x -> Seq.isEmpty x |> not

    let hasRemoteFileChanges groupName =
        match remoteFileChangesPerGroup |> Map.tryFind groupName with
        | None -> false
        | Some x -> Seq.isEmpty x |> not

    let hasChangedSettings groupName =
        match dependenciesFile.Groups |> Map.tryFind groupName with
        | None -> true
        | Some dependenciesFileGroup -> 
            match lockFile.Groups |> Map.tryFind groupName with
            | None -> true
            | Some lockFileGroup ->
                let lockFileGroupOptions =
                    if dependenciesFileGroup.Options.Settings.FrameworkRestrictions = AutoDetectFramework then
                        { lockFileGroup.Options with Settings = { lockFileGroup.Options.Settings with FrameworkRestrictions = AutoDetectFramework } }
                    else
                        lockFileGroup.Options
                dependenciesFileGroup.Options <> lockFileGroupOptions

    let hasChanges groupName _ = 
        hasChangedSettings groupName || hasNuGetChanges groupName || hasRemoteFileChanges groupName
        
    let hasAnyChanges =
        dependenciesFile.Groups
        |> Map.filter hasChanges
        |> Map.isEmpty
        |> not

    hasAnyChanges,nuGetChanges,remoteFileChanges,hasChanges