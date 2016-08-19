﻿namespace BioFSharp.Mz


open BioFSharp
open BioFSharp.IO

open System
open FSharp.Care
open FSharp.Care.Collections
open FSharp.Care.Monads
open AminoAcids 
open ModificationInfo
//open BioSequences

module SearchDB =

    open System.Data.SQLite
    open Either

    type SearchModType =
        | Minus
        | Plus 

    type SearchModSite =
        | Any      of ModificationInfo.ModLocation
        | Specific of AminoAcids.AminoAcid * ModificationInfo.ModLocation 
    
    type SearchModification = {
        Name        : string
        Accession   : string
        Description : string
        Composition : Formula.Formula
        Site        : SearchModSite list 
        MType       : SearchModType
        XModCode    : string
        }

    type MassMode = 
        | Average
        | Monoisotopic
        override this.ToString() = 
            match this with
            | Average -> "Average"
            | Monoisotopic -> "Monoisotopic"

    type SearchDbParams = {
        // name of database i.e. Creinhardtii_236_protein_full_labeled
        Name            : string
        // path of db storage folder
        DbFolder        : string
        FastaPath       : string
        Protease        : Digestion.Protease
        MissedCleavages : int
        MaxMass         : float
        // valid symbol name of isotopic label in label table i.e. #N15
        
        IsotopicLabel   : string // Change to global modification and make optional
        MassMode        : MassMode

        FixedMods       : SearchModification list            
        VariableMods    : SearchModification list
        }

    let createSearchDbParams name dbPath fastapath protease missedCleavages maxmass isotopicLabel massMode fixedMods variableMods = {
         Name=name; 
         DbFolder=dbPath; 
         FastaPath=fastapath; 
         Protease=protease; 
         MissedCleavages=missedCleavages; 
         MaxMass=maxmass; 
         IsotopicLabel=isotopicLabel; 
         MassMode=massMode; 
         FixedMods=List.sort fixedMods; 
         VariableMods=List.sort variableMods
         }

    ///needed as input if element of SearchModSite is of UnionCase | Any
    let private listOfAA = [
        AminoAcid.Ala; 
        AminoAcid.Cys; 
        AminoAcid.Asp; 
        AminoAcid.Glu; 
        AminoAcid.Phe; 
        AminoAcid.Gly; 
        AminoAcid.His; 
        AminoAcid.Ile; 
        AminoAcid.Lys; 
        AminoAcid.Leu; 
        AminoAcid.Met; 
        AminoAcid.Asn; 
        AminoAcid.Pyl; 
        AminoAcid.Pro; 
        AminoAcid.Gln; 
        AminoAcid.Arg; 
        AminoAcid.Ser; 
        AminoAcid.Thr; 
        AminoAcid.Sel; 
        AminoAcid.Val; 
        AminoAcid.Trp; 
        AminoAcid.Tyr
        ]


    // Record for a peptide sequence and its precalculated mass calcualted mass
    type PeptideWithMass<'a> = {
        Sequence : 'a
        Mass     : float
    }

    /// Creates PeptideWithMass record
    let createPeptideWithMass sequence mass = 
        {Sequence=sequence; Mass=mass}

    // Record for a peptide sequence and a container for all modified peptides
    type PeptideContainer = {
        PeptideId    : int    
        Sequence     : string
        GlobalMod    : string

        MissCleavageStart : int
        MissCleavageEnd   : int
        MissCleavageCount : int
        
        Container    : PeptideWithMass<string> list 
    }

    let createPeptideContainer peptideId sequence globalMod missCleavageStart missCleavageEnd missCleavageCount container =
        {PeptideId=peptideId; Sequence=sequence; GlobalMod=globalMod; MissCleavageStart=missCleavageStart; 
            MissCleavageEnd=missCleavageStart; MissCleavageCount=missCleavageCount; Container=container;}


    type ProteinContainer = {
        ProteinId    : int
        DisplayID    : string
        Sequence     : string
        Container    : PeptideContainer list
    }

    let createProteinContainer proteinId displayID sequence container = 
        {ProteinId=proteinId;DisplayID=displayID;Sequence=sequence;Container=container}


    type LookUpResult = {
        PepSequenceID : int
        RealMass      : int 
        RoundedMass   : float 
        Sequence      : string 
        GlobalMod     : string                
    }


    module Db =
        
        open System.Data.SQLite
        open Either

        type SqlErrorCode =
            | DbDataBaseNotFound
            | DbInternalLogicError
            | DbAccessDenied
            | DBGeneric of string * int
            | UnknownSqlException of SQLiteException
            | Unknown of Exception

        let sqlErrorCodeFromException (ex: Exception) =
            match ex with
            | :? SQLiteException  as ex ->
                    match ex.ErrorCode with
                    | 1 -> SqlErrorCode.DbDataBaseNotFound
                    | 2 -> SqlErrorCode.DbInternalLogicError
                    | 3 -> SqlErrorCode.DbAccessDenied
                    | _ -> SqlErrorCode.UnknownSqlException ex
            |  _ ->  SqlErrorCode.Unknown ex

        type SqlAction =
            | Create
            | Select
            | Insert
            | Delet
            | Update

        type PeptideLookUpError =
            | DbProtein of SqlAction * SqlErrorCode
            | DbProteinItemNotFound
            | DbCleavageIndex of SqlAction * SqlErrorCode 
            | DbCleavageIndexItemNotFound
            | DbPepSequence of SqlAction * SqlErrorCode
            | DbPepSequenceItemNotFound
            | DbModSequence of SqlAction * SqlErrorCode
            | DbModSequenceItemNotFound
            | DbSearchmodification of SqlAction * SqlErrorCode
            | DbSearchmodificationItemNotFound
            | DbSearchParams of SqlAction * SqlErrorCode
            | DbSearchParamsItemNotFound
            | DbInitialisation of SqlAction * SqlErrorCode
            | DbInitialisation_Database_Matching_The_Selected_Parameters_Already_Exists
            | DbInitialisation_Database_With_Identical_Name_But_Different_Parameters_Already_Exists


        ///  Prepared statements via Closure
        module internal SQLiteQuery =
    
            open System.Data
            open System.Data.SQLite


            //Create DB table statements
            /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            /// Creates Table SearchDbParams
            let createTableSearchDbParams (cn:SQLiteConnection) = 
                let querystring = 
                    "CREATE TABLE SearchDbParams (ID INTEGER, 
                                                 Name TEXT NOT NULL, 
                                                 DbFolder TEXT NOT NULL, 
                                                 FastaPath  TEXT NOT NULL, 
                                                 Protease  TEXT NOT NULL, 
                                                 MissedCleavages  INTEGER NOT NULL,
                                                 MaxMass  INTEGER NOT NULL, 
                                                 IsotopicLabel  TEXT NOT NULL, 
                                                 MassMode  TEXT NOT NULL, 
                                                 FixedMods TEXT NOT NULL,   
                                                 VariableMods TEXT NOT NULL,   
                                                 PRIMARY KEY (ID ASC)
                                                 )"
                let cmd  = new SQLiteCommand(querystring, cn)
                try
                    let exec = cmd.ExecuteNonQuery()
                    if  exec < 1 then
                        Either.succeed cn
                    else 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create, DBGeneric ("TABLE SearchDbParams",exec)) 
                        |> Either.fail
                with            
                | _ as ex -> 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create,sqlErrorCodeFromException ex) 
                        |> Either.fail


            /// Creates Table Protein
            let createTableProtein  (cn:SQLiteConnection) =
                let querystring = 
                    "CREATE TABLE Protein (ID  INTEGER,
                                           Accession  TEXT NOT NULL,
                                           Sequence  TEXT NOT NULL,
                                           PRIMARY KEY (ID ASC)
                                           )"
                let cmd = new SQLiteCommand(querystring, cn)
                try
                    let exec = cmd.ExecuteNonQuery()
                    if  exec < 1 then
                        Either.succeed cn
                    else 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create, DBGeneric ("TABLE Protein",exec)) 
                        |> Either.fail
                with            
                | _ as ex -> 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create,sqlErrorCodeFromException ex) 
                        |> Either.fail


            /// Creates Table CleavageIndex
            let createTableCleavageIndex  (cn:SQLiteConnection) = 
                let querystring = 
                    "CREATE TABLE CleavageIndex (ID INTEGER, 
                                                 ProteinID INTEGER NOT NULL, 
                                                 PepSequenceID INTEGER NOT NULL, 
                                                 CleavageStart  INTEGER NOT NULL, 
                                                 CleavageEnd  INTEGER NOT NULL, 
                                                 MissCleavages  INTEGER NOT NULL, 
                                                 PRIMARY KEY (ID ASC),
                                                 CONSTRAINT ProteinID FOREIGN KEY (ProteinID) REFERENCES Protein (ID),
                                                 CONSTRAINT PepSequenceID FOREIGN KEY (PepSequenceID) REFERENCES PepSequence (ID)
                                                 )"
                let cmd = new SQLiteCommand(querystring, cn)
                try
                    let exec = cmd.ExecuteNonQuery()
                    if  exec < 1 then
                        Either.succeed cn
                    else 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create, DBGeneric ("TABLE CleavageIndex",exec)) 
                        |> Either.fail
                with            
                | _ as ex -> 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create,sqlErrorCodeFromException ex) 
                        |> Either.fail


            /// Creates Table PepSequence
            let createTablePepSequence  (cn:SQLiteConnection) = 
                let querystring = 
                    "CREATE TABLE PepSequence (ID INTEGER,
                                               Sequence TEXT NOT NULL COLLATE NOCASE ,
                                               PRIMARY KEY (ID ASC),
                                               CONSTRAINT PepSequenceUnique UNIQUE (Sequence ASC) ON CONFLICT IGNORE
                                               )"
                let cmd = new SQLiteCommand(querystring, cn)
                try
                    let exec = cmd.ExecuteNonQuery()
                    if  exec < 1 then
                        Either.succeed cn
                    else 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create, DBGeneric ("TABLE PepSequence",exec)) 
                        |> Either.fail
                with            
                | _ as ex -> 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create,sqlErrorCodeFromException ex) 
                        |> Either.fail


            /// Creates Table ModSequence
            let createTableModSequence  (cn:SQLiteConnection) =
                let querystring = 
                    "CREATE TABLE ModSequence (ID	INTEGER,
	                                           PepSequenceID INTEGER NOT NULL,
	                                           RealMass REAL NOT NULL,
	                                           RoundedMass INTEGER NOT NULL,
	                                           Sequence TEXT NOT NULL,
	                                           GlobalMod TEXT NOT NULL,
	                                           PRIMARY KEY (ID),
	                                           FOREIGN KEY (PepSequenceID) REFERENCES PepSequence (ID) 
                                               )"
                let cmd = new SQLiteCommand(querystring, cn)
                try
                    let exec = cmd.ExecuteNonQuery()
                    if  exec < 1 then
                        Either.succeed cn
                    else 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create, DBGeneric ("TABLE ModSequence",exec)) 
                        |> Either.fail
                with            
                | _ as ex -> 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create,sqlErrorCodeFromException ex) 
                        |> Either.fail


            //Create Index Statements
            /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            let setMassIndexOnModSequence (cn:SQLiteConnection) = 
                let querystring = "CREATE INDEX RoundedMassIndex ON ModSequence (RoundedMass ASC) "
                let cmd = new SQLiteCommand(querystring, cn)    
                try
                    let exec = cmd.ExecuteNonQuery()
                    if  exec < 1 then
                        Either.succeed cn
                    else
                        PeptideLookUpError.DbInitialisation (SqlAction.Create, DBGeneric ("INDEX RoundedMassIndex",exec)) 
                        |> Either.fail
                with            
                | _ as ex -> 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create,sqlErrorCodeFromException ex) 
                        |> Either.fail


            let setSequenceIndexOnPepSequence (cn:SQLiteConnection) = 
                let querystring = "CREATE INDEX SequenceIndex ON PepSequence (Sequence ASC) "
                let cmd = new SQLiteCommand(querystring, cn)    
                try
                    let exec = cmd.ExecuteNonQuery()
                    if  exec < 1 then
                        Either.succeed cn
                    else 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create, DBGeneric ("INDEX SequenceIndex",exec)) 
                        |> Either.fail
                with            
                | _ as ex -> 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create,sqlErrorCodeFromException ex) 
                        |> Either.fail


            //Manipulate Pragma Statements
            /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            let pragmaSynchronousOFF (cn:SQLiteConnection) = 
                let querystring = "PRAGMA synchronous = 0 "
                let cmd = new SQLiteCommand(querystring, cn)
                // result equals number of affected rows
                try
                    let exec = cmd.ExecuteNonQuery()
                    if  exec > 0 then
                        Either.succeed cn
                    else 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create, DBGeneric ("PRAGMA synchronous",exec)) 
                        |> Either.fail
                with            
                | _ as ex -> 
                        PeptideLookUpError.DbInitialisation (SqlAction.Create,sqlErrorCodeFromException ex) 
                        |> Either.fail

         
            //Insert Statements
            /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            /// Prepares statement to insert a Protein entry
            let prepareInsertProtein (cn:SQLiteConnection) (tr) =
                let querystring = "INSERT INTO Protein (ID, Accession, Sequence) VALUES (@id, @accession, @sequence)"
                let cmd = new SQLiteCommand(querystring, cn, tr)
                cmd.Parameters.Add("@id", Data.DbType.Int32) |> ignore
                cmd.Parameters.Add("@accession", Data.DbType.String) |> ignore
                cmd.Parameters.Add("@sequence", Data.DbType.String) |> ignore
    
                (fun (id:int32) (accession:string) (sequence:string)  ->  
                        cmd.Parameters.["@id"].Value        <- id
                        cmd.Parameters.["@accession"].Value <- accession
                        cmd.Parameters.["@sequence"].Value  <- sequence
                        // result equals number of affected rows
                        cmd.ExecuteNonQuery()
                        )

   
            /// Prepares statement to insert a CleavageIndex entry
            let prepareInsertCleavageIndex (cn:SQLiteConnection) (tr) =
                let querystring = "INSERT INTO CleavageIndex (ProteinID, 
                                                              PepSequenceID, 
                                                              CleavageStart, 
                                                              CleavageEnd, 
                                                              MissCleavages) 
                                                              VALUES (@proteinID, 
                                                                      @pepSequenceID, 
                                                                      @cleavageStart, 
                                                                      @cleavageEnd, 
                                                                      @missCleavages)"
                let cmd = new SQLiteCommand(querystring, cn, tr)
                //  cmd.Parameters.Add("@id", Data.DbType.Int64) |> ignore
                cmd.Parameters.Add("@proteinID", Data.DbType.Int32) |> ignore
                cmd.Parameters.Add("@pepSequenceID", Data.DbType.Int32) |> ignore
                cmd.Parameters.Add("@cleavageStart", Data.DbType.Int32) |> ignore
                cmd.Parameters.Add("@cleavageEnd", Data.DbType.Int32) |> ignore
                cmd.Parameters.Add("@missCleavages", Data.DbType.Int32) |> ignore
    
                (fun  (proteinID:int32) (pepSequenceID:int32) (cleavageStart:int32) (cleavageEnd:int32) (missCleavages:int32)  -> // (id:uint64)
                        // cmd.Parameters.["@id"].Value            <- id
                        cmd.Parameters.["@proteinID"].Value     <- proteinID
                        cmd.Parameters.["@pepSequenceID"].Value <- pepSequenceID
                        cmd.Parameters.["@cleavageStart"].Value <- cleavageStart
                        cmd.Parameters.["@cleavageEnd"].Value   <- cleavageEnd
                        cmd.Parameters.["@missCleavages"].Value <- missCleavages
                        // result equals number of affected rows
                        cmd.ExecuteNonQuery()
                        )


            /// Prepares statement to insert a PepSequence entry
            let prepareInsertPepSequence (cn:SQLiteConnection) (tr) =
                let querystring = "INSERT INTO PepSequence (ID, Sequence) VALUES (@id, @sequence)"
                let cmd = new SQLiteCommand(querystring, cn, tr)
                cmd.Parameters.Add("@id", Data.DbType.Int32) |> ignore
                cmd.Parameters.Add("@sequence", Data.DbType.String) |> ignore
    
                (fun (id:int32) (sequence:string)  ->
                        cmd.Parameters.["@id"].Value  <- id
                        cmd.Parameters.["@sequence"].Value  <- sequence
                        // result equals number of affected rows
                        cmd.ExecuteNonQuery()
                        )



            /// Prepares statement to insert a ModSequence entry
            let prepareInsertModSequence (cn:SQLiteConnection) (tr) =
                let querystring = "INSERT INTO ModSequence (PepSequenceID, 
                                                            RealMass, 
                                                            RoundedMass, 
                                                            Sequence, 
                                                            GlobalMod) 
                                                            VALUES (@pepSequenceID, 
                                                                    @realmass, 
                                                                    @roundedmass, 
                                                                    @sequence, 
                                                                    @globalMod)" //ID, @id
                let cmd = new SQLiteCommand(querystring, cn, tr)
                //cmd.Parameters.Add("@id", Data.DbType.Int64) |> ignore
                cmd.Parameters.Add("@pepSequenceID", Data.DbType.Int32) |> ignore
                cmd.Parameters.Add("@realmass", Data.DbType.Double) |> ignore
                cmd.Parameters.Add("@roundedmass", Data.DbType.Int64) |> ignore        
                cmd.Parameters.Add("@sequence", Data.DbType.String) |> ignore
                cmd.Parameters.Add("@globalMod", Data.DbType.String) |> ignore
    
                (fun  (pepSequenceID:int32) (realmass: float) (roundedmass: int64) (sequence: string) (globalMod:string)  -> //(id:uint64)
                        // cmd.Parameters.["@id"].Value            <- id            
                        cmd.Parameters.["@pepSequenceID"].Value <- pepSequenceID
                        cmd.Parameters.["@realmass"].Value     <- realmass
                        cmd.Parameters.["@roundedmass"].Value     <- roundedmass
                        cmd.Parameters.["@sequence"].Value      <- sequence
                        cmd.Parameters.["@globalMod"].Value          <- globalMod
                        // result equals number of affected rows
                        cmd.ExecuteNonQuery()
                        )

            /// Prepares statement to insert a SearchDBParams entry
            let prepareInsertSearchDbParams (cn:SQLiteConnection) =
                let querystring = "INSERT INTO SearchDbParams (Name,
                                                               DbFolder, 
                                                               FastaPath, 
                                                               Protease, 
                                                               MissedCleavages, 
                                                               MaxMass, 
                                                               IsotopicLabel, 
                                                               MassMode, 
                                                               FixedMods, 
                                                               VariableMods) 
                                                               VALUES (@name, 
                                                                       @dbFolder, 
                                                                       @fastaPath, 
                                                                       @protease, 
                                                                       @missedCleavages, 
                                                                       @maxMass, 
                                                                       @isotopicLabel, 
                                                                       @massMode, 
                                                                       @fixedMods, 
                                                                       @variableMods)"
                let cmd = new SQLiteCommand(querystring, cn)
                cmd.Parameters.Add("@name", Data.DbType.String) |> ignore
                cmd.Parameters.Add("@dbFolder", Data.DbType.String) |> ignore
                cmd.Parameters.Add("@fastaPath", Data.DbType.String) |> ignore
                cmd.Parameters.Add("@protease", Data.DbType.String) |> ignore
                cmd.Parameters.Add("@missedCleavages", Data.DbType.Int32) |> ignore
                cmd.Parameters.Add("@maxMass", Data.DbType.Double) |> ignore
                cmd.Parameters.Add("@isotopicLabel", Data.DbType.String) |> ignore
                cmd.Parameters.Add("@massMode", Data.DbType.String) |> ignore
                cmd.Parameters.Add("@fixedMods", Data.DbType.String) |> ignore
                cmd.Parameters.Add("@variableMods", Data.DbType.String) |> ignore 
                (fun (name:string) (dbFolder:string) (fastaPath:string) (protease:string) (missedCleavages:int32) (maxMass:float) 
                    (isotopicLabel:string) (massMode:string) (fixedMods:string) (variableMods:string)  ->  
                        cmd.Parameters.["@name"].Value             <- name
                        cmd.Parameters.["@dbFolder"].Value         <- dbFolder
                        cmd.Parameters.["@fastaPath"].Value        <- fastaPath
                        cmd.Parameters.["@protease"].Value         <- protease
                        cmd.Parameters.["@missedCleavages"].Value  <- missedCleavages
                        cmd.Parameters.["@maxMass"].Value          <- maxMass
                        cmd.Parameters.["@isotopicLabel"].Value    <- isotopicLabel
                        cmd.Parameters.["@massMode"].Value         <- massMode
                        cmd.Parameters.["@fixedMods"].Value        <- fixedMods
                        cmd.Parameters.["@variableMods"].Value     <- variableMods
                        // result equals number of affected rows
                        cmd.ExecuteNonQuery()
                        )
                    
            //Select Statements
            /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            /// Prepares statement to select all SearchDbParams entries by FastaPath, Protease MissedCleavages, MaxMass, IsotopicLabel, MassMode, FixedMods, VariableMods
            let prepareSelectSearchDbParamsbyParams (cn:SQLiteConnection) =
                let querystring = "SELECT * FROM SearchDbParams WHERE FastaPath=@fastaPath 
                                                                AND Protease=@protease 
                                                                AND MissedCleavages=@missedCleavages 
                                                                AND MaxMass=@maxMass 
                                                                AND IsotopicLabel=@isotopicLabel 
                                                                AND MassMode=@massMode 
                                                                AND FixedMods=@fixedMods 
                                                                AND VariableMods=@variableMods"
                let cmd = new SQLiteCommand(querystring, cn) 
                cmd.Parameters.Add("@fastaPath", Data.DbType.String) |> ignore
                cmd.Parameters.Add("@protease", Data.DbType.String) |> ignore  
                cmd.Parameters.Add("@missedCleavages", Data.DbType.Int32) |> ignore  
                cmd.Parameters.Add("@maxMass", Data.DbType.Double) |> ignore  
                cmd.Parameters.Add("@isotopicLabel", Data.DbType.String) |> ignore  
                cmd.Parameters.Add("@massMode", Data.DbType.String) |> ignore  
                cmd.Parameters.Add("@fixedMods", Data.DbType.String) |> ignore  
                cmd.Parameters.Add("@variableMods", Data.DbType.String) |> ignore       
                (fun (fastaPath:string) (protease:string) (missedCleavages:int32) (maxMass:float) 
                    (isotopicLabel:string) (massMode:string) (fixedMods:string) (variableMods:string) ->         
                    cmd.Parameters.["@fastaPath"].Value <- fastaPath
                    cmd.Parameters.["@protease"].Value <- protease
                    cmd.Parameters.["@missedCleavages"].Value <- missedCleavages
                    cmd.Parameters.["@maxMass"].Value <- maxMass
                    cmd.Parameters.["@isotopicLabel"].Value <- isotopicLabel
                    cmd.Parameters.["@massMode"].Value <- massMode
                    cmd.Parameters.["@fixedMods"].Value <- fixedMods
                    cmd.Parameters.["@variableMods"].Value <- variableMods
                    try         
                        use reader = cmd.ExecuteReader()            
                        match reader.Read() with
                        | true -> (reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), 
                                    reader.GetInt32(5), reader.GetDouble(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10))
                                   |> Either.succeed
                        | false -> PeptideLookUpError.DbSearchParamsItemNotFound
                                    |> Either.fail

             
                    with            
                    | _ as ex -> 
                        PeptideLookUpError.DbSearchParams (SqlAction.Select,sqlErrorCodeFromException ex) 
                        |> Either.fail
                )

            /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            /// Prepares statement to select a Protein entry by ID        
            let prepareSelectProteinByID (cn:SQLiteConnection) (tr) =
                let querystring = "SELECT * FROM Protein WHERE ID=@id "
                let cmd = new SQLiteCommand(querystring, cn, tr) 
                cmd.Parameters.Add("@id", Data.DbType.Int32) |> ignore       
                (fun (id:int32)  ->         
                    cmd.Parameters.["@id"].Value <- id
                    try
                        
                        use reader = cmd.ExecuteReader()            
                        match reader.Read() with
                        | true -> (reader.GetInt32(0),reader.GetString(1), reader.GetString(2)) |> Either.succeed
                        | false -> PeptideLookUpError.DbProteinItemNotFound
                                    |> Either.fail

             
                    with            
                    | _ as ex -> 
                        PeptideLookUpError.DbProtein (SqlAction.Select,sqlErrorCodeFromException ex) 
                        |> Either.fail
                )
            /// Prepares statement to select a Protein entry by Accession     
            let prepareSelectProteinByAccession (cn:SQLiteConnection) (tr) =
                let querystring = "SELECT * FROM Protein WHERE Accession=@accession"
                let cmd = new SQLiteCommand(querystring, cn, tr) 
                cmd.Parameters.Add("@accession", Data.DbType.String) |> ignore       
                (fun (accession:string)  ->         
                    cmd.Parameters.["@accession"].Value <- accession
        
                    try
                        
                        use reader = cmd.ExecuteReader()            
                        match reader.Read() with
                        | true -> (reader.GetInt32(0),reader.GetString(1), reader.GetString(2)) |> Either.succeed
                        | false -> PeptideLookUpError.DbProteinItemNotFound
                                    |> Either.fail

             
                    with            
                    | _ as ex -> 
                        PeptideLookUpError.DbProtein (SqlAction.Select,sqlErrorCodeFromException ex) 
                            |> Either.fail
                )
            /// Prepares statement to select a Protein entry by Sequence     
            let prepareSelectProteinBySequence (cn:SQLiteConnection) (tr) =
                let querystring = "SELECT * FROM Protein WHERE Sequence=@sequence"
                let cmd = new SQLiteCommand(querystring, cn, tr) 
                cmd.Parameters.Add("@sequence", Data.DbType.String) |> ignore       
                (fun (sequence:string)  ->         
                    cmd.Parameters.["@sequence"].Value <- sequence
        
                    try
                        
                        use reader = cmd.ExecuteReader()            
                        match reader.Read() with
                        | true -> (reader.GetInt32(0),reader.GetString(1), reader.GetString(2)) |> Either.succeed
                        | false -> PeptideLookUpError.DbProteinItemNotFound
                                    |> Either.fail

             
                    with            
                    | _ as ex -> 
                        PeptideLookUpError.DbProtein (SqlAction.Select,sqlErrorCodeFromException ex) 
                        |> Either.fail
                )

            //////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            /// Prepares statement to select a CleavageIndex entry ProteinID 
            let prepareSelectCleavageIndexByProteinID (cn:SQLiteConnection) (tr) =
                let querystring = "SELECT * FROM CleavageIndex WHERE ProteinID=@proteinID"
                let cmd = new SQLiteCommand(querystring, cn, tr) 
                cmd.Parameters.Add("@proteinID", Data.DbType.Int64) |> ignore       
                (fun (proteinID:int32)  ->         
                    cmd.Parameters.["@proteinID"].Value <- proteinID
        
                    try
                        
                        use reader = cmd.ExecuteReader()            
                        match reader.Read() with
                        | true -> (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)) 
                                  |> Either.succeed
                        | false -> PeptideLookUpError.DbCleavageIndexItemNotFound
                                    |> Either.fail

             
                    with            
                    | _ as ex -> 
                        PeptideLookUpError.DbCleavageIndex (SqlAction.Select,sqlErrorCodeFromException ex) 
                        |> Either.fail
                )

            /// Prepares statement to select a CleavageIndex entry PepSequenceID 
            let prepareSelectCleavageIndexByPepSequenceID  (cn:SQLiteConnection) (tr) =
                let querystring = "SELECT * FROM CleavageIndex WHERE PepSequenceID=@pepSequenceID"
                let cmd = new SQLiteCommand(querystring, cn, tr) 
                cmd.Parameters.Add("@pepSequenceID", Data.DbType.Int32) |> ignore       
                (fun (pepSequenceID:int32)  ->         
                    cmd.Parameters.["@pepSequenceID"].Value <- pepSequenceID
        
                    try
                        
                        use reader = cmd.ExecuteReader()            
                        match reader.Read() with
                        | true -> (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)) 
                                  |> Either.succeed
                        | false -> PeptideLookUpError.DbCleavageIndexItemNotFound
                                    |> Either.fail

             
                    with            
                    | _ as ex -> 
                        PeptideLookUpError.DbCleavageIndex (SqlAction.Select,sqlErrorCodeFromException ex) 
                        |> Either.fail
                )

            //////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            /// Prepares statement to select a PepSequence entry by PepSequence 
            let prepareSelectPepSequenceBySequence' (cn:SQLiteConnection) (tr) =
                let querystring = "SELECT * FROM PepSequence WHERE Sequence=@sequence"
                let cmd = new SQLiteCommand(querystring, cn, tr) 
                cmd.Parameters.Add("@sequence", Data.DbType.String) |> ignore       
                (fun (sequence:string)  ->         
                    cmd.Parameters.["@sequence"].Value <- sequence
                    try       
                        use reader = cmd.ExecuteReader()
                        match reader.Read() with 
                        | true ->  reader.GetInt32(0) |> Either.succeed         
                        | false -> PeptideLookUpError.DbPepSequenceItemNotFound
                                   |> Either.fail


                    with
                    | _ as ex -> 
                        PeptideLookUpError.DbCleavageIndex (SqlAction.Select,sqlErrorCodeFromException ex)
                        |> Either.fail
                )
            /// Prepares statement to select a PepSequence entry by PepSequence - Version without try.. with pattern to enhance the Select performance
            let prepareSelectPepSequenceBySequence (cn:SQLiteConnection) (tr) =
                let querystring = "SELECT * FROM PepSequence WHERE Sequence=@sequence"
                let cmd = new SQLiteCommand(querystring, cn, tr) 
                cmd.Parameters.Add("@sequence", Data.DbType.String) |> ignore       
                (fun (sequence:string)  ->         
                    cmd.Parameters.["@sequence"].Value <- sequence       
                    use reader = cmd.ExecuteReader()
                    reader.Read() |> ignore 
                    reader.GetInt32(0)           
                    )

            //////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            /// Prepares statement to select a ModSequence entry by PepSequenceID
            let prepareSelectModsequenceByPepSequenceID (cn:SQLiteConnection) (tr) =
                let querystring = "SELECT * FROM ModSequence WHERE PepSequenceID=@pepSequenceID"
                let cmd = new SQLiteCommand(querystring, cn, tr) 
                cmd.Parameters.Add("@pepSequenceID", Data.DbType.Int32) |> ignore       
                (fun (pepSequenceID:int32)  ->        
                    cmd.Parameters.["@pepSequenceID"].Value <- pepSequenceID
                    try
                        
                        use reader = cmd.ExecuteReader()            
                        match reader.Read() with
                        | true ->  (reader.GetInt32(0), reader.GetInt32(1),reader.GetDouble(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5)) 
                                   |> Either.succeed
                        | false -> PeptideLookUpError.DbModSequenceItemNotFound
                                    |> Either.fail

             
                    with            
                    | _ as ex -> 
                        PeptideLookUpError.DbModSequence (SqlAction.Select,sqlErrorCodeFromException ex) 
                        |> Either.fail
                )

            /// Prepares statement to select a ModSequence entry by Mass
            let prepareSelectModsequenceByMass (cn:SQLiteConnection) (tr) =
                let querystring = "SELECT * FROM ModSequence WHERE Mass=@mass"
                let cmd = new SQLiteCommand(querystring, cn, tr) 
                cmd.Parameters.Add("@mass", Data.DbType.Int32) |> ignore       
                (fun (mass: int)  ->        
                    cmd.Parameters.["@mass"].Value <- mass
                    try
                        
                        use reader = cmd.ExecuteReader()            
                        match reader.Read() with
                        | true -> (reader.GetInt32(0), reader.GetInt32(1),reader.GetDouble(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5))  
                                  |> Either.succeed
                        | false -> PeptideLookUpError.DbModSequenceItemNotFound
                                    |> Either.fail

             
                    with            
                    | _ as ex -> 
                        PeptideLookUpError.DbModSequence (SqlAction.Select,sqlErrorCodeFromException ex) 
                        |> Either.fail
                )

            /// Prepares statement to select a ModSequence entry by Massrange (Between selected Mass -/+ the selected toleranceWidth
            let prepareSelectModsequenceByMassRange (cn:SQLiteConnection) (mass1:int64) (mass2:int64) =
                let querystring = "SELECT * FROM ModSequence WHERE RoundedMass BETWEEN @mass1 AND @mass2"
                let cmd = new SQLiteCommand(querystring, cn) 
                cmd.Parameters.Add("@mass1", Data.DbType.Int64) |> ignore
                cmd.Parameters.Add("@mass2", Data.DbType.Int64) |> ignore
                let rec readerloop (reader:SQLiteDataReader) (acc:(int*int*float*int64*string*string) list) =
                        match reader.Read() with 
                        | true  -> readerloop reader (( reader.GetInt32(0), reader.GetInt32(1),reader.GetDouble(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5) ) :: acc)
                        | false ->  acc 

                cmd.Parameters.["@mass1"].Value <- mass1
                cmd.Parameters.["@mass2"].Value <- mass2
                
                use reader = cmd.ExecuteReader()            
                readerloop reader [] 

            /// Prepares statement to select a ModSequence entry by Sequence
            let prepareSelectModsequenceBySequence (cn:SQLiteConnection) (tr) =
                let querystring = "SELECT * FROM ModSequence WHERE Sequence=@sequence"
                let cmd = new SQLiteCommand(querystring, cn, tr) 
                cmd.Parameters.Add("@sequence", Data.DbType.Double) |> ignore       
                (fun (sequence:string)  ->        
                    cmd.Parameters.["@sequence"].Value <- sequence
                    try
                                    
                        use reader = cmd.ExecuteReader()            
                        match reader.Read() with
                        | true -> (reader.GetInt32(0), reader.GetInt32(1),reader.GetDouble(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5))  
                                  |> Either.succeed
                        | false -> PeptideLookUpError.DbModSequenceItemNotFound
                                    |> Either.fail

             
                    with            
                    | _ as ex -> 
                        PeptideLookUpError.DbModSequence (SqlAction.Select,sqlErrorCodeFromException ex) 
                        |> Either.fail
                )

                /// Prepares statement to select all SearchDbParams entries by ID
            let prepareSelectSearchDbParams (cn:SQLiteConnection) =
                let querystring = "SELECT * FROM SearchDbParams"
                let cmd = new SQLiteCommand(querystring, cn) 
                cmd.Parameters.Add("@id", Data.DbType.Int32) |> ignore       
                (fun (id:int32)  ->         
                    cmd.Parameters.["@id"].Value <- id
                    try         
                        use reader = cmd.ExecuteReader()            
                        match reader.Read() with
                        | true -> (reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), 
                                    reader.GetInt32(5), reader.GetDouble(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10))
                                    |> Either.succeed
                        | false -> PeptideLookUpError.DbProteinItemNotFound
                                    |> Either.fail

             
                    with            
                    | _ as ex -> 
                        PeptideLookUpError.DbSearchParams (SqlAction.Select,sqlErrorCodeFromException ex) 
                        |> Either.fail
                )


                //#define SQLITE_ERROR        1   /* SQL error or missing database */
                //#define SQLITE_INTERNAL     2   /* Internal logic error in SQLite */
                //#define SQLITE_PERM         3   /* Access permission denied */
                //#define SQLITE_ABORT        4   /* Callback routine requested an abort */
                //#define SQLITE_BUSY         5   /* The database file is locked */
                //#define SQLITE_LOCKED       6   /* A table in the database is locked */
                //#define SQLITE_NOMEM        7   /* A malloc() failed */
                //#define SQLITE_READONLY     8   /* Attempt to write a readonly database */
                //#define SQLITE_INTERRUPT    9   /* Operation terminated by sqlite3_interrupt()*/
                //#define SQLITE_IOERR       10   /* Some kind of disk I/O error occurred */
                //#define SQLITE_CORRUPT     11   /* The database disk image is malformed */
                //#define SQLITE_NOTFOUND    12   /* Unknown opcode in sqlite3_file_control() */
                //#define SQLITE_FULL        13   /* Insertion failed because database is full */
                //#define SQLITE_CANTOPEN    14   /* Unable to open the database file */
                //#define SQLITE_PROTOCOL    15   /* Database lock protocol error */
                //#define SQLITE_EMPTY       16   /* Database is empty */
                //#define SQLITE_SCHEMA      17   /* The database schema changed */
                //#define SQLITE_TOOBIG      18   /* String or BLOB exceeds size limit */
                //#define SQLITE_CONSTRAINT  19   /* Abort due to constraint violation */
                //#define SQLITE_MISMATCH    20   /* Data type mismatch */
                //#define SQLITE_MISUSE      21   /* Library used incorrectly */
                //#define SQLITE_NOLFS       22   /* Uses OS features not supported on host */
                //#define SQLITE_AUTH        23   /* Authorization denied */
                //#define SQLITE_FORMAT      24   /* Auxiliary database format error */
                //#define SQLITE_RANGE       25   /* 2nd parameter to sqlite3_bind out of range */
                //#define SQLITE_NOTADB      26   /* File opened that is not a database file */
                //#define SQLITE_NOTICE      27   /* Notifications from sqlite3_log() */
                //#define SQLITE_WARNING     28   /* Warnings from sqlite3_log() */
                //#define SQLITE_ROW         100  /* sqlite3_step() has another row ready */
                //#define SQLITE_DONE        101  /* sqlite3_step() has finished executing */





        /// Returns the database name given the SearchDbParams
        let getNameOf (sdbParams:SearchDbParams) =
            sprintf "%s\\%s.db" 
                (sdbParams.DbFolder |> FSharp.Care.IO.PathFileName.normalizeFileName)
                (sdbParams.Name |> FSharp.Care.IO.PathFileName.fileNameWithoutExtension )
    
        /// Returns a comma seperated string of given search modification list
        let getModStringOf (searchMods:SearchModification list) =
            searchMods
            |> List.map (fun a -> a.Name)
            |> String.concat ", "        

        /// Inserts SearchDbParams into DB
        let insertSdbParams cn (sdbParams:SearchDbParams) =         
            SQLiteQuery.prepareInsertSearchDbParams cn sdbParams.Name sdbParams.DbFolder sdbParams.FastaPath sdbParams.Protease.Name
                sdbParams.MissedCleavages sdbParams.MaxMass sdbParams.IsotopicLabel (sdbParams.MassMode.ToString())
                (getModStringOf sdbParams.FixedMods) (getModStringOf sdbParams.VariableMods)

        /// Select SearchDbParams entry from DB by given SearchDbParams
        let selectSdbParamsby cn (sdbParams:SearchDbParams) = 
            SQLiteQuery.prepareSelectSearchDbParamsbyParams cn sdbParams.FastaPath sdbParams.Protease.Name sdbParams.MissedCleavages 
                sdbParams.MaxMass sdbParams.IsotopicLabel (sdbParams.MassMode.ToString()) 
                    (getModStringOf sdbParams.FixedMods) (getModStringOf sdbParams.VariableMods)  
    
        /// Returns true if a db exists with the same parameter content
        let isExistsBy (sdbParams:SearchDbParams) =       
            let fileName = getNameOf sdbParams
            match FSharp.Care.IO.FileIO.fileExists fileName with 
            | true  -> 
                let connectionString = sprintf "Data Source=%s;Version=3" fileName
                use cn = new SQLiteConnection(connectionString)
                cn.Open()
                match selectSdbParamsby cn sdbParams with
                | Success _   -> true
                | Failure _   -> false                                                                           
            | false -> false

    
        /// Create a new file instance of the DB schema. Deletes already existing instance.
        let initDB fileName =
    
            let _ = FSharp.Care.IO.FileIO.DeleteFile fileName 

            let connectionString = sprintf "Data Source=%s;Version=3" fileName
            use cn = new SQLiteConnection(connectionString)
  
            let initDB' =
                switch (tee (fun (cn:SQLiteConnection) -> cn.Open()))
                >=> SQLiteQuery.createTableSearchDbParams
                >=> SQLiteQuery.createTableProtein
                >=> SQLiteQuery.createTableCleavageIndex
                >=> SQLiteQuery.createTablePepSequence
                >=> SQLiteQuery.createTableModSequence
                >=> SQLiteQuery.setSequenceIndexOnPepSequence
                >=> switch (tee (fun cn -> cn.Close()))
        
            initDB' cn


        /// Bulk insert for a sequence of ProteinContainers
        let bulkInsert (cn:SQLiteConnection) (data:seq<ProteinContainer>) =
            cn.Open()
            let tr = cn.BeginTransaction()
            // Bind Insert and Select statements to connection and transaction / prepares statements
            let insertProtein = SQLiteQuery.prepareInsertProtein cn tr
            let insertCleavageIndex = SQLiteQuery.prepareInsertCleavageIndex cn tr
            let insertPepSequence = SQLiteQuery.prepareInsertPepSequence cn tr
            let selectPepSequenceBySequence = SQLiteQuery.prepareSelectPepSequenceBySequence cn tr
            let insertModSequence = SQLiteQuery.prepareInsertModSequence cn tr

            data
            |> Seq.iter 
                (fun (protContainer) ->
                    match insertProtein protContainer.ProteinId protContainer.DisplayID protContainer.Sequence with                                                                        
                    | 1 -> protContainer.Container
                           |> List.iter 
                                (fun pepContainer -> 
                                    match insertPepSequence pepContainer.PeptideId pepContainer.Sequence with                         
                                    | 1 ->  insertCleavageIndex protContainer.ProteinId  pepContainer.PeptideId pepContainer.MissCleavageStart
                                                pepContainer.MissCleavageEnd pepContainer.MissCleavageCount|>ignore
                                            pepContainer.Container
                                            |> List.iter (fun modPep -> 
                                                            insertModSequence (protContainer.ProteinId*10000+pepContainer.PeptideId) modPep.Mass ((Convert.ToInt64(modPep.Mass*1000000.)))
                                                                modPep.Sequence pepContainer.GlobalMod |> ignore
                                                            )  

                                    | _  -> insertCleavageIndex protContainer.ProteinId (selectPepSequenceBySequence pepContainer.Sequence) 
                                                pepContainer.MissCleavageStart pepContainer.MissCleavageEnd pepContainer.MissCleavageEnd |>ignore                         
                                )   

                    | _ -> printfn "Protein is already in the database" |>ignore
                )


            SQLiteQuery.setMassIndexOnModSequence cn |> ignore
            //Commit and dispose the transaction and close the SQLiteConnection
            tr.Commit()
            tr.Dispose()
            cn.Close()


    module ModCombinator =    
    
        open FSharp.Care.Collections
        open BioFSharp    
        open BioFSharp.AminoAcids
        open BioFSharp.ModificationInfo
    
        open Db

        /// Type abreviation
        type ModLookUpFunc = AminoAcid -> Modification list option

        type ModLookUp = {
            ResidualFixed :            ModLookUpFunc
            NTermAndResidualFixed :    ModLookUpFunc
            CTermAndResidualFixed :    ModLookUpFunc
            ResidualVariable :         ModLookUpFunc
            NTermAndResidualVariable : ModLookUpFunc
            CTermAndResidualVariable : ModLookUpFunc
            Total: string -> string option
            Global : GlobalModificationInfo.GlobalModification<AminoAcid> option
            }

        /// Flag indicates if potential modification is fixed
        [<Struct>]
        type AminoAcidWithFlag(flag:bool,amino:AminoAcid) =
            member this.IsFlaged  = flag
            member this.AminoAcid = amino
            new (amino) = AminoAcidWithFlag(false,amino)

        let isVarModified (a:AminoAcidWithFlag) =
            a.IsFlaged
    

        let setVarModifiedFlagOf (a:AminoAcid)  =
            AminoAcidWithFlag(true,a)

        let setFixedModifiedFlagOf (a:AminoAcid)  =
            AminoAcidWithFlag(false,a)

    
        
        /// Returns a list of all possible modified AminoAcids given the particular Searchmodification
        // param: aminoAcids is a list of all possible aminoacids
        let convertSearchModification (aminoAcids: AminoAcid list) (searchModification:SearchModification) =
            ///Creates AminoAcid Modification tuple. Concerns MType of the Searchmodification  
            let modificationOf (searchMod : SearchModification) modLocation =
                match searchMod.MType with
                | SearchModType.Plus  -> createModificationWithAdd searchMod.Name modLocation searchMod.Composition
                | SearchModType.Minus -> createModificationWithSubstract searchMod.Name modLocation searchMod.Composition
                       
            ///Matches elements of the SearchModification.Site with the SearchModSites Any or Specific; Returns tuple of AminoAcids and Modifications
            searchModification.Site 
            |> List.collect (fun site  ->                          
                                match site with
                                |Any(modLoc) -> 
                                    let tmpModification = modificationOf searchModification modLoc
                                    aminoAcids |> List.map (fun a -> (a,tmpModification))                                                                                                                                        
                                |Specific(aa, modLoc)->  
                                    let tmpModification = modificationOf searchModification modLoc
                                    [(aa,tmpModification)]
                            ) 
        

        /// Returns the ModLookup according to given SearchDbParams
        let modLookUpOf (dbParams:SearchDbParams) =         
        
            //Filters list of AminoAcid Modification tuples depending on the used logexp    
            let createAndFilterBy (logexp: AminoAcid*Modification -> bool) searchModifications = 
                searchModifications
                |> List.collect (convertSearchModification listOfAA)
                |> List.filter (logexp: _*Modification -> bool)
                |> Map.compose

            let aminoParser (c:char) : AminoAcid =
                match AminoAcids.charToParsedAminoAcidChar c with
                | AminoAcids.ParsedAminoAcidChar.StandardCodes code -> code
                | AminoAcids.ParsedAminoAcidChar.AmbiguityCodes code -> code
                | _ -> failwithf "Wrong Format in global mod string."


            ///Logexp that returns true if ModLocation of modified AminoAcid equals Residual 
            let residual (_,modi) =  ModLocation.Residual.Equals(modi.Location)
            ///Logexp that returns true if ModLocation of modified AminoAcid equals Residual, Nterm or ProteinNterm 
            let nTermAndResidual (_,modi) = 
                ModLocation.Nterm.Equals(modi.Location)||ModLocation.ProteinNterm.Equals(modi.Location) || ModLocation.Residual.Equals(modi.Location)
            ///Logexp that returns true if ModLocation of modified AminoAcid equals Residual, Cterm or ProteinCterm  
            let cTermAndResidual (_,modi) = 
                ModLocation.Cterm.Equals(modi.Location)||ModLocation.ProteinCterm.Equals(modi.Location) || ModLocation.Residual.Equals(modi.Location)
            {                                                      
                ResidualFixed=
                    let lookUpR = createAndFilterBy residual dbParams.FixedMods
                    fun aa -> Map.tryFind aa lookUpR     
                NTermAndResidualFixed=
                    let lookUpNR = createAndFilterBy nTermAndResidual dbParams.FixedMods
                    fun aa -> Map.tryFind aa lookUpNR      
                CTermAndResidualFixed=
                    let lookUpCR = createAndFilterBy cTermAndResidual dbParams.FixedMods
                    fun aa -> Map.tryFind aa lookUpCR     
                ResidualVariable =
                    let lookUpR = createAndFilterBy residual dbParams.VariableMods
                    fun aa -> Map.tryFind aa lookUpR     
                NTermAndResidualVariable =
                    let lookUpNR = createAndFilterBy nTermAndResidual dbParams.VariableMods
                    fun aa -> Map.tryFind aa lookUpNR      
                CTermAndResidualVariable =
                    let lookUpCR = createAndFilterBy cTermAndResidual dbParams.VariableMods
                    fun aa -> Map.tryFind aa lookUpCR   
                Total=
                    let lookupT = 
                        (dbParams.FixedMods@dbParams.VariableMods)
                        |> List.map (fun searchmod -> searchmod.Name, searchmod.XModCode)  
                        |> Map.ofList
                    fun aa -> Map.tryFind aa lookupT
                Global = Some (GlobalModificationInfo.ofString aminoParser dbParams.IsotopicLabel)
             }

        ///Returns modified or unmodified AminoAcid depending on the matching expression in a AminoAcidWithFlag struct
        ///The boolean value "false" is used to state that the Modification is fixed    
        let setFixModByLookUp (modLookUpFunc:ModLookUpFunc) (aa: AminoAcidWithFlag) =
            match modLookUpFunc aa.AminoAcid with
            | Some modiList -> setFixedModifiedFlagOf (AminoAcids.setModifications modiList aa.AminoAcid)
            | None -> aa     


        ///Returns modified or unmodified AminoAcid depending on the matching expression in a AminoAcidWithFlag struct
        ///The boolean value "false" is used to state that the Modification is fixed    
        let setVarModByLookUp (modLookUpFunc:ModLookUpFunc) (aa: AminoAcidWithFlag) =
            match modLookUpFunc aa.AminoAcid with
            | Some modiList -> 
                        let tmp = 
                            modiList 
                            |> List.map (fun modi -> setVarModifiedFlagOf (AminoAcids.setModification modi aa.AminoAcid))               
                        aa::tmp
            | None -> [aa] 
                                                    

        /// Returns a list of all possible modified petide sequences and its masses according to the given modification-lookUp.
        /// It uses the given bioitem -> mass function and a function to aggregate the sequence.
        let combine (modLookUp:ModLookUp) threshold (massfunction:IBioItem -> float) seqfunction state (aal: AminoAcid list) =
            let rec loop c (massAcc:float) acc (aal: AminoAcid list) =
                match c with
                | c when c = threshold -> 
                    match aal with
                    | h::tail -> 
                        match tail with 
                        // Last amino acid => set NTermAndResidualFixed
                        | [] -> setFixModByLookUp modLookUp.NTermAndResidualFixed (AminoAcidWithFlag h) 
                        // all other amino acid => set ResidualFixed
                        | _  -> setFixModByLookUp modLookUp.ResidualFixed  (AminoAcidWithFlag h)
                        |> (fun item ->
                                match item.AminoAcid with
                                | Mod (a,m) -> let currentModMass = m |> List.fold (fun s x -> s + massfunction x) 0.0
                                               loop c (massAcc + currentModMass) (seqfunction acc item.AminoAcid) tail
                                | a         -> loop c massAcc  (seqfunction acc item.AminoAcid) tail  
                            )
                
                    | [] -> [createPeptideWithMass acc massAcc]
  
                                     
                | c -> 
                    match aal with
                    | h::tail -> 
                        match tail with
                        | [] -> 
                            AminoAcidWithFlag h 
                            |> setFixModByLookUp modLookUp.NTermAndResidualFixed 
                            |> setVarModByLookUp modLookUp.NTermAndResidualVariable 
                        | _  -> 
                            AminoAcidWithFlag h
                            |> setFixModByLookUp modLookUp.ResidualFixed  
                            |> setVarModByLookUp modLookUp.ResidualVariable
                        |> List.collect (fun item ->
                                                match (isVarModified item),item.AminoAcid with
                                                | true,Mod (a,m)   -> 
                                                    let currentModMass = m |> List.fold (fun s x -> s + massfunction x) 0.0
                                                    loop (c+1) (massAcc + currentModMass) (seqfunction acc item.AminoAcid) tail                                       
                                                | false, Mod (a,m) -> 
                                                    let currentModMass = m |> List.fold (fun s x -> s + massfunction x) 0.0
                                                    loop c (massAcc + currentModMass) (seqfunction acc item.AminoAcid) tail
                                                | false, a         -> 
                                                    loop c massAcc (seqfunction acc item.AminoAcid) tail 
                                                | true,_ -> failwith "Matching case impossible: Check AminoAcidWithFlag"
                                            )                   
                                                   
                    | [] ->  [createPeptideWithMass acc massAcc]   

            let massOfPeptide =  
                if modLookUp.Global.IsSome then
                     aal
                    |> List.fold (fun s x -> s + (massfunction x) + (modLookUp.Global.Value.Modifiy x)) 0.0 //TODO: add water
                else 
                    aal
                    |> List.fold (fun s x -> s + massfunction x) 0.0 //TODO: add water
            loop 0 massOfPeptide state (aal |> List.rev)



        /// Returns a ModString representation. 
        let ToModStringBy (xModLookUp:string->string option) (aa: AminoAcid) =
            match (aa:AminoAcid) with
            | AminoAcids.Mod (aa, mds) ->  
                                    mds
                                    |> List.fold (fun acc (md:Modification) -> match xModLookUp md.Name with
                                                                                | Some x -> x + acc 
                                                                                | None -> failwithf "Failed"                                                                                         
                                                                                ) "" 
                                    |> (fun x -> x + ((BioItem.symbol aa).ToString()))
                                                     
            | aa -> ((BioItem.symbol aa).ToString())  


        /// Returns a list of all possible modified petide sequences and its masses according to the given modification-lookUp.
        /// The peptide sequence representation is ModString.
        let combineToModString (modLookUp:ModLookUp) threshold (massfunction:IBioItem -> float) (aal: AminoAcid list) =
            let toString = ToModStringBy modLookUp.Total
            let seqfunction = (fun state amino -> state + (toString amino))
            combine modLookUp threshold massfunction seqfunction "" aal
    




   
    // --------------------------------------------------------------------------------
    // PeptideLookUp continues

    let getPeptideLookUpFromFile dbFileName = 
        let connectionString = sprintf "Data Source=%s;Version=3" dbFileName
        let cn = new SQLiteConnection(connectionString)
        cn.Open()
        let selectModsequenceByMassRange = Db.SQLiteQuery.prepareSelectModsequenceByMassRange cn
        (fun lowerMass upperMass  -> 
                let lowerMass' = Convert.ToInt64(lowerMass*1000000.)
                let upperMass' = Convert.ToInt64(upperMass*1000000.)
                selectModsequenceByMassRange lowerMass' upperMass') 
    
    
    // TODO: place into SearchDbParams record
    // fastaHeaderToName
    // threshold
    // massfunction
    // Digestion filter params 
    let getPeptideLookUpBy (sdbParams:SearchDbParams) (fastaHeaderToName:string->string) threshold massfunction =
        // Check existens by param
        let dbFileName = Db.getNameOf sdbParams
        if Db.isExistsBy sdbParams then            
            getPeptideLookUpFromFile dbFileName        
        else
            // Create db file
            match Db.initDB dbFileName with
            | Failure _ -> failwith "Error"
            | Success _ ->
                // prepares LookUpMaps of modLookUp based on the dbParams
                let modLookUp = ModCombinator.modLookUpOf sdbParams
                // Set name of global modification
                let globalMod = if modLookUp.Global.IsNone then "" else modLookUp.Global.Value.Name
                let connectionString = sprintf "Data Source=%s;Version=3" dbFileName
                let cn = new SQLiteConnection(connectionString)
                cn.Open()
                let _ = Db.insertSdbParams cn sdbParams
                cn.Close()
                // Read Fasta
                let fasta = 
                    BioFSharp.IO.FastA.fromFile (BioArray.ofAminoAcidString) sdbParams.FastaPath                
                // Digest
                fasta
                |> Seq.mapi 
                    (fun i fastaItem ->
                        let proteinId = i // TODO                        
                        let peptideContainer =
                            Digestion.BioArray.digest Digestion.trypsin proteinId fastaItem.Sequence
                            |> Digestion.BioArray.concernMissCleavages 1 3 // TODO 
                            |> Array.filter (fun x -> 
                                                let cleavageRange = x.MissCleavageEnd - x.MissCleavageStart
                                                cleavageRange > 4 && cleavageRange < 63 // TODO
                                            )
                            |> Array.mapi (fun peptideId pep ->
                                                let container = 
                                                    ModCombinator.combineToModString modLookUp threshold massfunction pep.PepSequence
                                                createPeptideContainer (proteinId*10000+peptideId) (BioList.toString pep.PepSequence) globalMod pep.MissCleavageStart pep.MissCleavageEnd pep.MissCleavages container
                                            )
                        
                        createProteinContainer 
                            proteinId 
                                (fastaHeaderToName fastaItem.Header) 
                                    (BioArray.toString fastaItem.Sequence) 
                                        (peptideContainer |> List.ofArray)
                    ) 
                |> Db.bulkInsert cn
                |> ignore                
                
                getPeptideLookUpFromFile dbFileName


