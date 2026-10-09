namespace VaiDrikstu.Domain;

public static class IntentCatalog
{
    public static readonly IntentDefinition[] All =
    [
        new(
            "solar-panels",
            "Uzstādīt saules paneļus",
            "Pārbaudīt, vai adresei var būt kultūras mantojuma vai dabas teritorijas ierobežojumi.",
            ["Pārbaudiet būvvaldes prasības BIS.", "Ja objekts ir aizsargājams, saskaņojiet ieceri ar kompetento iestādi."]),
        new(
            "signboard",
            "Izvietot izkārtni",
            "Pārbaudīt, vai izkārtnei var būt nepieciešama saskaņošana adreses vai objekta statusa dēļ.",
            ["Sagatavojiet izkārtnes vizualizāciju.", "Sazinieties ar pašvaldības būvvaldi vai reklāmas saskaņošanas speciālistu."]),
        new(
            "facade-windows",
            "Mainīt logus vai fasādi",
            "Pārbaudīt, vai ēkai var būt mantojuma vai teritorijas aizsardzības nosacījumi.",
            ["Pārbaudiet, vai darbi maina ēkas ārējo veidolu.", "Ja ir ierobežojumi, pirms darbu sākšanas vajadzīga oficiāla saskaņošana."]),
        new(
            "terrace",
            "Izvietot āra terasi",
            "Pārbaudīt, vai vietai var būt publiskās ārtelpas, mantojuma vai dabas teritorijas nosacījumi.",
            ["Sagatavojiet novietojuma skici.", "Pārbaudiet pašvaldības kārtību āra terašu saskaņošanai."]),
        new(
            "small-structure",
            "Būvēt nelielu palīgēku",
            "Pārbaudīt sākotnējos atvērtajos datos redzamos ierobežojumus pirms BIS procesa.",
            ["Pārbaudiet, vai iecerei vajadzīgs paskaidrojuma raksts vai būvniecības iecere BIS.", "Pārbaudiet zemesgabala izmantošanas nosacījumus."])
    ];

    public static IntentDefinition? Find(string id) =>
        All.FirstOrDefault(intent => string.Equals(intent.Id, id, StringComparison.OrdinalIgnoreCase));
}
