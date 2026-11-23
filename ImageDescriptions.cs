using System.Collections.Generic;

namespace Killer_Frequency_Access;

// Hand-written descriptions of what boards, maps and clippings show as pictures. This is a hand
// translation, like the verb table: written from the game's own textures (tools\rip\extract_textures.py),
// read and described by a person. The game's own text for each item (its GameText entry) is spoken
// first; these add what the text leaves out: the picture, and where things are on a map.
//
// Facts that are not from a picture say where they come from:
// - The maize maze answers are the game's own: the Maize_Sequence flow graph sends Left at the tractor
//   statue, Backwards at the pig statue, Left at the pitchfork statue, Right at the tiny barn and Right
//   at the corn silo on to the next junction; every other option sets a "Tried" fact and costs a strike.
// - Which evidence polaroid each object shows: SM_EvidenceMap_Location_01..06 UVs, 01 top left of the
//   atlas to 06 bottom right.
internal static class ImageDescriptions
{
    /// <summary>Picture description per GameText entry name (the text panel's source).</summary>
    internal static readonly Dictionary<string, string> ByGameTextKey = new()
    {
        // Maize Maze map (Barbara's, found in the trash with Brad's note).
        ["MaizeMaze.MapFront"] =
            "Picture: a fold-out map. On the left, a square maze of pale paths between green corn hedges, with pine trees around it. " +
            "The entrance and the exit are one gap in the middle of the top edge, marked with a down arrow and an up arrow. " +
            "A pond with a tree sits in the middle of the maze. " +
            "Ten numbered wooden markers stand at junctions. Top left: 9, corn silo. Left edge, upper: 10, bee hives. " +
            "Left side, middle: 6, scarecrow, with 7, farmer hat, just below and left of it. Lower left: 8, mini barn. " +
            "Top middle, near the entrance: 5, pitchfork statue. Right side, upper: 4, the horse statue, with 3, pig statue, just right of it. " +
            "Right side, middle: 1, tractor statue, with 2, golden hay bales, just below it. " +
            "On the right of the map, the legend shows a silhouette for each landmark. " +
            "Read with Eugene's calls, the map gives this way out. Eugene calls the horse statue a rocking horse and the mini barn a tiny barn. " +
            "Facing the tractor statue with the gold hay bales on his right: go left. " +
            "Pig statue in front, rocking horse on his left: go backwards. " +
            "Crossroads with the pitchfork statue up ahead: go left. " +
            "Tiny barn ahead, scarecrow behind: go right. " +
            "Just passed the corn silo: go right, and he is out. " +
            "Every other choice is a wrong turn and costs him a strike.",
        ["MaizeMazeAsset.MapBack"] =
            "Picture: the folded map's cover. MAIZE MAZE in big yellow letters on a green banner, above a small drawing of maze paths.",
        ["MaizeMazeAsset.BradDumpNote"] =
            "Picture: a handwritten note in black marker on grey paper.",

        // Gallows Creek map hint cards and traffic notice (Incinerator call).
        ["IncineratorAsset.Alex"] =
            "Picture: a handwritten card pinned to the Gallows Creek map. On the map, Haddonfield Road meets Romero Street in the east of town, " +
            "between Wood Side Apartments' block and the Sheriff's Office.",
        ["IncineratorAsset.Katherine"] =
            "Picture: a handwritten card pinned to the Gallows Creek map. On the map, the west end of Myers Lane is where it meets Rogers Avenue, " +
            "just east of Sanders Drive-in Theater, north of Macready Street.",
        ["IncineratorAsset.Jericho"] =
            "Picture: a handwritten card pinned to the Gallows Creek map. On the map, the east end of Myers Lane is where it meets Carpenter Avenue, " +
            "south of Ironside Power Station, north of Macready Street.",
        ["IncineratorAsset.FireDepartment"] =
            "Picture: a handwritten card pinned to the Gallows Creek map. On the map, the GCFD Fire Station is in the south west of town, " +
            "between Jackson Street and Jones Road, just east of Thornside Road.",
        ["IncineratorAsset.TrafficNotice"] =
            "Picture: a printed notice with a red heading, pinned to the Gallows Creek map, signed in ink. " +
            "On the map, Macready Street runs east and west between Myers Lane and Romero Street. The closed stretch runs from Rogers Avenue east, " +
            "past the Trailer Park, Wood Side Apartments, Carpenter Avenue and Weyland Road, to Haddonfield Road.",

        // Gallows Creek map labels. North is up. Romero Street, the dark highway, crosses the whole map west
        // to east and splits the town into a north half and a south half.
        ["Map.WelcomeMessage"] =
            "Picture: a wooden sign in the south east corner of the Gallows Creek map. The map has north at the top, and a compass rose in the south west corner, by a lake. " +
            "Romero Street, a dark highway, crosses the whole map from west to east and splits the town into a north half and a south half. " +
            "A railway, drawn as a yellow dotted line, loops around the north and west of town. A river runs down the east side.",
        ["Map.HellandFalls"] = "On the map: the far north west corner, in the woods outside town, inside the railway loop.",
        ["Map.LoramieEstate"] = "On the map: the north west, a big house in the woods, north of Loramie Way and west of the farm with Giblet Field.",
        ["Map.Cemetary"] = "On the map: the west edge of town, south of Loramie Way, west of Thornside Road, with Roller Ricky's to its south. Rows of grave stones.",
        ["Map.RollerRickys"] = "On the map: the west, west of Thornside Road, just north of Romero Street, south of West Gallows Cemetery.",
        ["Map.JazzStudio"] = "On the map: the south west, outside town, west of Thornside Road and the railway, on the north side of Clarke Road.",
        ["Map.TrainStation"] = "On the map: the south west, on the railway west of Thornside Road, just south of Clarke Road.",
        ["Map.ToReservoir"] = "On the map: the south west corner, a road leading off the map to the west, beside a lake.",
        ["Map.GibletField"] =
            "On the map: the north, a large farm north of Peele Street and east of Thornside Road. Giblet Field is the big field on its east side. " +
            "Farm buildings stand along its north edge, and its south west corner holds a small drawn maze, the Maize Maze.",
        ["Map.PowerStation"] =
            "On the map: the north, north of Peele Street, between the farm with Giblet Field to the west and Carpenter Avenue to the east. " +
            "Shepherd's Walk runs along its north side. Drawn with round tanks.",
        ["Map.WasteDisposal"] =
            "On the map: the north east, at the north end of Weyland Road, on its east side, north of Peele Street. " +
            "The railway and the river end beside it, and Deerfall Woodlands lie to its east.",
        ["Map.Ravine"] = "On the map: the far north east corner, outside town, grey rocks where the river comes from.",
        ["Map.DeerfallWoodlands"] = "On the map: the east, woods east of Gallows Waste Disposal, outside town.",
        ["Map.WelcomeCenter"] =
            "On the map: the east, in the woods east of Haddonfield Road, level with Myers Lane. A road runs south from it and then west to Haddonfield Road, at Macready Street.",
        ["Map.SheriffsOffice"] = "On the map: the east, east of Haddonfield Road, just north of Romero Street.",
        ["Map.WhistlingPoint"] = "On the map: the east, in the woods beyond Paragon Street, south of Romero Street, beside the river.",
        ["Map.DriveInTheatre"] = "On the map: north of the highway, between Peele Street and Macready Street, from Thornside Road east to Rogers Avenue. Drawn with a screen and parked cars.",
        ["Map.Hospital"] = "On the map: north of the highway, between Macready Street and Romero Street, just east of Thornside Road. Doc's Pharmacy is next to it on the east.",
        ["Map.Pharmacy"] = "On the map: north of the highway, between Macready Street and Romero Street, east of St. Gabriel's Hospital and west of Rogers Avenue.",
        ["Map.TrailerPark"] = "On the map: north of the highway, between Macready Street and Romero Street, east of Rogers Avenue. Drawn with rows of trailers.",
        ["Map.WoodSideApts"] = "On the map: north of the highway, between Macready Street and Romero Street, east of the Trailer Park and west of Carpenter Avenue.",
        ["Map.PriceSlashers"] =
            "On the map: south of the highway, one block between Thornside Road and Blake Avenue. From north to south: Come On Inn on Romero Street, " +
            "then Price Slashers Supermarket, then Christine's Gas and Repair on Bond Street.",
        ["Map.GrillingSpree"] =
            "On the map: south of the highway, one block between Blake Avenue and King Avenue. From north to south: The Barker Public Library on Romero Street, " +
            "then Grilling Spree, then the Gallows Reporter on Bond Street.",
        ["Map.TownHall"] = "On the map: south of the highway, on Romero Street, east of a row of shops and west of the Ambrosia Hotel. Mason Street is to its south.",
        ["Map.Hotel"] = "On the map: south of the highway, on Romero Street, east of the Town Hall and west of Paragon Street. Ripley Records is just south of it.",
        ["Map.RipleyRecords"] = "On the map: south of the highway, west of Paragon Street, between the Ambrosia Hotel to the north and Little Shop of Flowers to the south.",
        ["Map.FlowerShop"] = "On the map: south of the highway, west of Paragon Street, south of Ripley Records, on the north side of Hooper Street.",
        ["Map.Arcade"] = "On the map: south of the highway, south of Mason Street, east of King Avenue, next to KFAM on its west.",
        ["Map.KFAMRadioStation"] =
            "On the map: the middle of the south half, drawn in red: your station. South of Mason Street, with Infinity Arcade to the west, " +
            "Cine Film Picture House to the east and Rise and Shine Coffee just south.",
        ["Map.PictureHouse"] = "On the map: south of the highway, south of Mason Street, just east of KFAM.",
        ["Map.CoffeeShop"] = "On the map: south of the highway, just south of KFAM and Infinity Arcade, on the north side of Nancy Drive.",
        ["Map.VideoRental"] = "On the map: south of the highway, between Nancy Drive and Axe-Down Lane, south of Rise and Shine Coffee.",
        ["Map.Chalupacabras"] = "On the map: south of the highway, on the south side of Axe-Down Lane, south of Rewind Video Rentals, between Craven Street and Newbury Lane.",
        ["Map.Church"] = "On the map: south of the highway, west of Paragon Street, between Hooper Street to the north and Axe-Down Lane to the south.",
        ["Map.GallowsCreekHigh"] = "On the map: the south east, south of Axe-Down Lane, west of Paragon Street. Its football field lies just south.",
        ["Map.Diner"] = "On the map: the south west, between Dinkley Drive and Jackson Street, east of the William Street houses.",
        ["Map.FireStation"] = "On the map: the south west, between Jackson Street and Jones Road, just east of Thornside Road.",
        ["Map.PontysPizza"] = "On the map: the south, between Jackson Street and Jones Road, east of the Fire Station.",
        ["Map.Factory"] = "On the map: the south, a long factory between Jackson Street and Jones Road, east of Ponty's Pizza.",
        ["Map.LoramieWay"] = "On the map: a short road in the north west, running west from the top of Thornside Road. Loramie Estate is north of it, West Gallows Cemetery south.",
        ["Map.PeeleStreet"] =
            "On the map: runs east and west across the north of town, from beside West Gallows Cemetery east to Haddonfield Road. " +
            "North of it: the farm with Giblet Field, Ironside Power Station and Gallows Waste Disposal. South of it: Sanders Drive-in and rows of houses.",
        ["Map.ShepherdsWalk"] = "On the map: a short road north of Ironside Power Station, from the east fence of Giblet Field east to the top of Carpenter Avenue.",
        ["Map.MyersLane"] =
            "On the map: a short lane north of the highway, between Peele Street and Macready Street, lined with houses and a small park. " +
            "Its west end meets Rogers Avenue, beside Sanders Drive-in. Its east end meets Carpenter Avenue.",
        ["Map.MacreadyStreet"] =
            "On the map: runs east and west north of the highway, between Myers Lane and Romero Street, from Thornside Road east to Haddonfield Road. " +
            "A road continues east from its end to the Deerfall Welcome Center.",
        ["Map.RomeroStreet"] = "On the map: the dark highway across the whole map from west to east, splitting the town into north and south.",
        ["Map.ThornsideRoad"] = "On the map: the long north to south road along the west side of town, from Loramie Way past Romero Street down to Jones Road.",
        ["Map.RogersAvenue"] = "On the map: north of the highway, runs north to south from Peele Street to Romero Street, between the Drive-in and Hospital to the west and Myers Lane and the Trailer Park to the east.",
        ["Map.CarpenterAvenue"] = "On the map: north of the highway, runs north to south from Shepherd's Walk past Ironside Power Station to Romero Street, the east end of Myers Lane.",
        ["Map.WeylandRoad"] = "On the map: north of the highway, runs north to south from Gallows Waste Disposal at the top of the map down to Romero Street, between Carpenter Avenue and Haddonfield Road.",
        ["Map.HaddonfieldRoad"] = "On the map: north of the highway, the easternmost street in town, running north to south from Peele Street to Romero Street. The Sheriff's Office and the Welcome Center are east of it.",
        ["Map.BlakeAvenue"] = "On the map: south of the highway, runs south from Romero Street to Bond Street, between Price Slashers and the Barker Public Library.",
        ["Map.KingAvenue"] = "On the map: south of the highway, runs south from Romero Street past the Gallows Reporter and Infinity Arcade to Craven Street.",
        ["Map.ParagonStreet"] = "On the map: south of the highway, the easternmost street, running south from Romero Street past the hotel, the church and Gallows Creek High. Woods and the river lie east of it.",
        ["Map.MasonStreet"] = "On the map: south of the highway, runs east and west from King Avenue, south of the Town Hall row and north of KFAM.",
        ["Map.HooperStreet"] = "On the map: south of the highway, a short street from Cine Film Picture House east to Paragon Street, between Little Shop of Flowers and St. Jason's Church.",
        ["Map.BondStreet"] = "On the map: south of the highway, runs east and west from Thornside Road to King Avenue, south of Christine's Gas and the Gallows Reporter.",
        ["Map.ClarkeRoad"] = "On the map: a short road west of Thornside Road, crossing the railway to the Jazz Studio, just north of the Train Station.",
        ["Map.NancyDrive"] = "On the map: south of the highway, runs east and west south of Bond Street, past Rise and Shine Coffee.",
        ["Map.AxeDownLane"] = "On the map: south of the highway, runs east and west from Thornside Road to Paragon Street, north of Chalupacabras and Gallows Creek High.",
        ["Map.CravenStreet"] = "On the map: south of the highway, a short street running south east from King Avenue, west of Chalupacabras.",
        ["Map.NewburyLane"] = "On the map: south of the highway, a short lane running south east from Axe-Down Lane down to Jackson Street, east of Chalupacabras.",
        ["Map.WilliamStreet"] = "On the map: the south west, a short street running east from Thornside Road, between Axe-Down Lane and Jackson Street.",
        ["Map.DinkleyDrive"] = "On the map: the south west, a short street just north of Mac's Diner.",
        ["Map.JacksonStreet"] = "On the map: runs east and west across the south of town, from Thornside Road to Paragon Street, south of Mac's Diner and north of the Fire Station and Ponty's Pizza.",
        ["Map.JonesRoad"] = "On the map: the southern edge of town, south of the Fire Station, Ponty's Pizza and Gallows and Sons.",

        // Alibi board (two copies: Janitor's office in the basement, and the main studio).
        ["AlibiAsset.ClassOf68"] =
            "Picture: a worn yearbook page with the school crest and a portrait photo for each student. Four faces are circled in red: " +
            "Kim Walker, Rebecca Allen, Ant Williams and Chuck Brody. Keith Walker, Marie Campbell, Stanley King and Billy Price are not circled. " +
            "This page is fixed to the board.",
        ["AlibiAsset.GallowsFootball"] =
            "Picture: a blue and cream magazine cover. A drawing of a young football player with curly dark hair in a white jersey, number 13, a stadium crowd behind him.",
        ["AlibiAsset.PowerStation"] = "Picture: a torn newspaper clipping, text only.",
        ["AlibiAsset.CarCrimewave"] = "Picture: a torn newspaper clipping, text only; the end is torn off.",
        ["AlibiAsset.FestivalDisaster"] = "Picture: a newspaper page with a photo of the Big Wheel, a broken Ferris wheel, at night.",
        ["AlibiAsset.ChuckDonations"] = "Picture: a handwritten note on stained, lined paper, ending with an arrow pointing down.",
        ["AlibiAsset.FestivalVictims"] = "Picture: a crumpled newspaper clipping, text only; the end is torn off.",
        ["AlibiAsset.MarriageAnnouncement"] = "Picture: a small framed newspaper notice, text only.",
        ["AlibiAsset.BusCrash"] = "Picture: a newspaper page with a photo of a burnt-out bus wreck.",
        ["AlibiAsset.SyndicateArrest"] = "Picture: a newspaper page with a photo of police cars behind police line tape.",
        ["AlibiAsset.FluShot"] = "Picture: a torn newspaper clipping, text only.",
        ["AlibiAsset.RealEstate"] = "Picture: a real estate advert with a photo of a white trailer numbered 101, a For Sale sign in its window, behind a picket fence.",
        ["AlibiAsset.LotteryWinner"] =
            "Picture: a newspaper page with a photo of a gas station, pumps under a canopy marked GAS. In the corner, a cut-out coupon " +
            "shows the book cover of Diary of a Car Thief with a smiling woman with long dark curly hair.",
        ["AlibiAsset.SoldTrailerPark"] = "Picture: a newspaper page with a photo of the same trailer numbered 101, now with a SOLD sign, and a police car in front.",
        ["AlibiAsset.HealthAndSafety"] = "Picture: a printed flyer. The mystery guest's photo is a black silhouette of a head with a white question mark.",
        ["AlibiAsset.ChuckBrody"] = "Picture: a yellow sticky note, handwritten, fixed to the board.",
        ["AlibiAsset.KimWalker"] = "Picture: a yellow sticky note, handwritten, fixed to the board.",
        ["AlibiAsset.RebeccaAllen"] = "Picture: a yellow sticky note, handwritten, fixed to the board.",
        ["AlibiAsset.AntWilliams"] = "Picture: a yellow sticky note, handwritten, fixed to the board.",
        ["AlibiAsset.GasStation"] = "Picture: a yellow sticky note, handwritten, fixed to the board.",
        ["AlibiAsset.PowerStationName"] = "Picture: a yellow sticky note, handwritten, fixed to the board.",
        ["AlibiAsset.Hospital"] = "Picture: a yellow sticky note, handwritten, fixed to the board.",
        ["AlibiAsset.TrailerPark"] = "Picture: a yellow sticky note, handwritten, fixed to the board.",
    };

    /// <summary>
    /// Picture description for items with no text of their own, keyed by the name of the item's
    /// GameObject or an ancestor (rip names; the game is frozen, so they do not change).
    /// </summary>
    internal static readonly Dictionary<string, string> ByObjectName = new()
    {
        // Save Clive: polaroids of basement spots.
        ["P_HeroProp_Interactable_Clive_EvidenceMap_Location_01"] =
            "A polaroid of a basement corner: metal shelves with cardboard boxes. A blue pen arrow points at the floor in front of the shelves, to a sketched cassette tape.",
        ["P_HeroProp_Interactable_Clive_EvidenceMap_Location_02"] =
            "A polaroid of a cluttered basement corner: a folded chair and a rolled rug in front, a small metal trolley with a drawer, shelves behind. " +
            "Two blue pen arrows point at the floor: the left one to a sketched notebook, the right one to a sketched cassette tape.",
        ["P_HeroProp_Interactable_Clive_EvidenceMap_Location_03"] =
            "A polaroid of shelves holding tape decks and other electronics, with boxes piled on the right. A blue pen arrow points at the floor at the bottom left, to a sketched cassette tape.",
        ["P_HeroProp_Interactable_Clive_EvidenceMap_Location_04"] =
            "A polaroid of a metal cabinet with two vented doors, an electric fan and a box on top. A blue pen arrow points at the floor in front of the cabinet, to a sketched cassette tape.",
        ["P_HeroProp_Interactable_Clive_EvidenceMap_Location_05"] =
            "A polaroid of tall blue lockers with papers on top, a low metal cabinet, and newspapers on the floor. " +
            "Two blue pen arrows point at the floor: the left one to a sketched notebook, by the newspapers, the right one to a sketched cassette tape, in front of the cabinet.",
        ["P_HeroProp_Interactable_Clive_EvidenceMap_Location_06"] =
            "A polaroid of an office corner: a desk with binders and a lamp, filing cabinets, and a box of files on the floor. A blue pen arrow points at the floor right of the desk, to a sketched cassette tape.",

        // Gallows Creek map: markers and pin tacks the player moves around the map.
        ["P_HeroProp_MapItem_Marker_01"] = "A red paper marker with a big letter A, for marking the map.",
        ["P_HeroProp_MapItem_Marker_02"] = "A green paper marker with a big letter B, for marking the map.",
        ["P_HeroProp_MapItem_Marker_03"] = "A blue paper marker with a big letter C, for marking the map.",
        ["P_HeroProp_MapItem_Marker_04"] = "A purple paper marker with a big letter D, for marking the map.",
        ["P_HeroProp_MapItem_Marker_05"] = "A yellow paper marker with a big letter E, for marking the map.",
        ["P_HeroProp_MapItem_PinTack_01"] = "A red pin tack with the number 1, for marking the map.",
        ["P_HeroProp_MapItem_PinTack_02"] = "A blue pin tack with the number 2, for marking the map.",
        ["P_HeroProp_MapItem_PinTack_03"] = "A yellow pin tack with the number 3, for marking the map.",
        ["P_HeroProp_MapItem_PinTack_04"] = "A green pin tack with the number 4, for marking the map.",
        ["P_HeroProp_MapItem_PinTack_05"] = "A purple pin tack with the number 5, for marking the map.",
    };
}
