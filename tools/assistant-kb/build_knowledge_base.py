#!/usr/bin/env python3
"""Builds the deterministic knowledge base for the result assistant ("palīgs").

The assistant does not use an LLM. Everything it can say comes from the JSON files
this script writes to ``data/assistant``:

* ``labels.json``      - dictionaries of codes/labels (reason codes, VZD encumbrance
                         codes, VZD cadastre marks, Riga zoning, protection zones,
                         protected nature areas, address-register columns ...)
* ``documents.json``   - regulation articles (likumi.lv snapshots, Riga TIAN zone
                         sections) and dataset descriptions, used for full-text search
* ``monuments.json``   - NKMP list of state protected cultural monuments
* ``cadastre-marks.json`` - VZD cadastre marks per cadastre designation

Usage (paths are the two folders delivered with the project)::

    python tools/assistant-kb/build_knowledge_base.py \
        --sources "C:/Users/<you>/Desktop/sources/sources" \
        --external "C:/Users/<you>/Desktop/data/data/external" \
        --out data/assistant

Only the Python standard library plus ``beautifulsoup4`` (HTML laws) is required.
The Riga TIAN PDF is read through ``pdftotext`` (poppler) when it is on PATH; when it
is missing the zone texts fall back to the short definitions kept below.
"""
from __future__ import annotations

import argparse
import collections
import csv
import glob
import json
import os
import re
import shutil
import sqlite3
import struct
import subprocess
import sys
import tempfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path

csv.field_size_limit(sys.maxsize)

# ---------------------------------------------------------------------------
# Hand-written content: reason codes of the rule engine (lv / ru / en)
# ---------------------------------------------------------------------------

REASON_CODES = {
    "ADDRESS_FOUND": (
        ("Adrese atrasta", "Adrese ir atrasta adrešu datos, un pārbaude veikta tās koordinātām."),
        ("Адрес найден", "Адрес найден в адресных данных, проверка выполнена по его координатам."),
        ("Address found", "The address was found in the address data and the check used its coordinates."),
    ),
    "ADDRESS_NOT_FOUND": (
        ("Adrese nav atrasta", "Adrese nav atrasta datu izgriezumā, tāpēc ierobežojumus nevarēja pārbaudīt. Precizējiet adresi Valsts adrešu reģistrā (VZD)."),
        ("Адрес не найден", "Адрес не найден в выборке данных, поэтому ограничения проверить нельзя. Уточните адрес в Государственном регистре адресов (VZD)."),
        ("Address not found", "The address is not in the data extract, so restrictions could not be checked. Verify it in the State Address Register (VZD)."),
    ),
    "HERITAGE_SITE_NEARBY": (
        ("Kultūras piemineklis tuvumā", "150 m rādiusā no adreses ir valsts aizsargājams kultūras piemineklis. Ap pieminekļiem parasti ir aizsardzības zona (Aizsargjoslu likums, 'Par kultūras pieminekļu aizsardzību'), kurā darbi, kas maina vidi vai ēku ārējo veidolu, jāsaskaņo ar Nacionālo kultūras mantojuma pārvaldi (NKMP)."),
        ("Рядом памятник культуры", "В радиусе 150 м от адреса находится охраняемый государством памятник культуры. Вокруг памятников обычно есть охранная зона, где работы, меняющие среду или внешний вид здания, согласуются с Национальным управлением культурного наследия (NKMP)."),
        ("Cultural monument nearby", "A state-protected cultural monument lies within 150 m of the address. Monuments usually have a protection zone where works changing the surroundings or a building's appearance must be agreed with the National Heritage Board (NKMP)."),
    ),
    "HERITAGE_SITE_INTERSECTS": (
        ("Adrese ir kultūras pieminekļa teritorijā", "Adreses punkts atrodas valsts aizsargājama kultūras pieminekļa teritorijā. Gandrīz visi darbi, kas ietekmē ēkas ārējo veidolu, jāsaskaņo ar NKMP."),
        ("Адрес на территории памятника", "Точка адреса находится на территории охраняемого памятника культуры. Почти любые работы, влияющие на внешний вид здания, нужно согласовать с NKMP."),
        ("Address inside a monument area", "The address point lies inside a protected cultural monument area. Almost any work that affects the building's appearance needs NKMP approval."),
    ),
    "NATURE_AREA_INTERSECTS": (
        ("Īpaši aizsargājama dabas teritorija", "Adrese atrodas īpaši aizsargājamā dabas teritorijā (ĪADT) vai Natura 2000 vietā. Darbībām var būt Dabas aizsardzības pārvaldes (DAP) nosacījumi un individuālie aizsardzības un izmantošanas noteikumi."),
        ("Особо охраняемая природная территория", "Адрес находится на особо охраняемой природной территории (ĪADT) или в зоне Natura 2000. Для работ могут действовать условия Управления охраны природы (DAP)."),
        ("Specially protected nature area", "The address lies in a specially protected nature area (ĪADT) or Natura 2000 site. Works may require conditions from the Nature Conservation Agency (DAP)."),
    ),
    "RED_LINE_INTERSECTS": (
        ("Šķērso ielas sarkano līniju", "Vieta atrodas ielas sarkanajās līnijās - teritorijā, kas paredzēta ielai un inženierkomunikācijām (Aizsargjoslu likums, 1. pants). Būvniecība tur parasti nav atļauta vai ir stingri ierobežota; jāsaskaņo ar pašvaldību."),
        ("Пересекает красную линию улицы", "Место находится в красных линиях улицы - территории, отведённой под улицу и коммуникации. Строительство там обычно запрещено или сильно ограничено; нужно согласование с самоуправлением."),
        ("Crosses a street red line", "The place is inside street red lines - land reserved for the street and utilities. Building there is usually not allowed or strictly limited; agree with the municipality."),
    ),
    "HERITAGE_PROTECTION_ZONE_INTERSECTS": (
        ("Kultūras mantojuma aizsardzības zona", "Vieta atrodas kultūras pieminekļa aizsardzības zonā, pilsētbūvniecības pieminekļa vai kultūrvēsturiskā teritorijā. Darbi, kas maina vidi vai ēku ārējo veidolu, jāsaskaņo ar NKMP."),
        ("Охранная зона культурного наследия", "Место находится в охранной зоне памятника, памятника градостроительства или культурно-исторической территории. Работы, меняющие среду или внешний вид зданий, согласуются с NKMP."),
        ("Heritage protection zone", "The place is inside a monument protection zone, urban heritage monument or cultural-historic territory. Works changing the surroundings or appearance of buildings need NKMP approval."),
    ),
    "FLOOD_RISK_INTERSECTS": (
        ("Applūšanas riska teritorija", "Vieta atrodas teritorijā ar applūšanas varbūtību (piem. reizi 10 gados). Applūstošajās teritorijās būvniecība ir ierobežota (Aizsargjoslu likums, 37. pants)."),
        ("Зона риска затопления", "Место находится на территории с вероятностью затопления (например, раз в 10 лет). На затапливаемых территориях строительство ограничено (закон об охранных зонах, ст. 37)."),
        ("Flood risk area", "The place is in an area with flood probability (e.g. once in 10 years). Building in floodplains is restricted (Protection Zone Law, Section 37)."),
    ),
    "PLANNING_ZONE_FOUND": (
        ("Noteikta funkcionālā zona", "Vietai atrasta teritorijas plānojuma funkcionālā zona (piem. DzS1, JC2). Zona nosaka atļauto izmantošanu, stāvu skaitu un apbūves parametrus - pajautājiet palīgam zonas kodu."),
        ("Найдена функциональная зона", "Для места найдена функциональная зона территориального плана (напр. DzS1, JC2). Зона определяет разрешённое использование, этажность и параметры застройки - спросите помощника код зоны."),
        ("Planning zone found", "A functional zone of the spatial plan was found (e.g. DzS1, JC2). It sets allowed use, number of floors and building parameters - ask the assistant about the zone code."),
    ),
    "RULE_DATA_COVERAGE_LIMITED": (
        ("Datu pārklājums ierobežots", "Šai vietai vai darbībai atvērtie dati vai noteikumu tabula nepārklāj visu - rezultāts var būt nepilnīgs. Precizējiet pašvaldībā vai būvvaldē."),
        ("Покрытие данных ограничено", "Открытые данные или таблица правил покрывают это место или действие не полностью - результат может быть неполным. Уточните в самоуправлении или стройуправе."),
        ("Limited data coverage", "Open data or the rule table do not fully cover this place or action - the result may be incomplete. Check with the municipality or building board."),
    ),
    "NO_RESTRICTION_FOUND_IN_OPEN_DATA": (
        ("Ierobežojumi nav atrasti", "Pārbaudītajos atvērtajos datos (kultūras mantojums, dabas teritorijas) izvēlētajai darbībai būtiski ierobežojumi nav atrasti. Tas nenozīmē atļauju: teritorijas plānojums, apgrūtinājumi un kopīpašnieku piekrišana netika pārbaudīti."),
        ("Ограничений не найдено", "В проверенных открытых данных (наследие, природные территории) существенных ограничений для выбранного действия не найдено. Это не разрешение: территориальный план, обременения и согласие совладельцев не проверялись."),
        ("No restriction found", "No relevant restriction was found in the checked open data (heritage, nature areas). This is not a permit: zoning, encumbrances and co-owner consent were not checked."),
    ),
    "OFFICIAL_REVIEW_REQUIRED": (
        ("Nepieciešama oficiāla izvērtēšana", "Rezultāts norāda, ka galīgo lēmumu jāpieņem kompetentajai iestādei (būvvaldei, NKMP, DAP vai pašvaldībai)."),
        ("Требуется официальная оценка", "Окончательное решение должно принять компетентное учреждение (стройуправа, NKMP, DAP или самоуправление)."),
        ("Official review required", "A competent authority (building board, NKMP, DAP or the municipality) has to make the final decision."),
    ),
    "NKMP_COORDINATION_LIKELY": (
        ("Visticamāk vajadzīga NKMP saskaņošana", "Izvēlētā darbība kultūras mantojuma objektā vai tā tuvumā parasti jāsaskaņo ar Nacionālo kultūras mantojuma pārvaldi (NKMP) pirms darbu sākšanas."),
        ("Вероятно, нужно согласование с NKMP", "Выбранное действие на объекте наследия или рядом с ним обычно согласуется с Национальным управлением культурного наследия (NKMP) до начала работ."),
        ("NKMP approval likely", "The selected action at or near a heritage object usually has to be agreed with the National Heritage Board (NKMP) before works start."),
    ),
    "DAP_CONDITIONS_LIKELY": (
        ("Visticamāk piemērojami DAP nosacījumi", "Dabas teritorijā izvēlētajai darbībai var būt vajadzīgi Dabas aizsardzības pārvaldes (DAP) tehniskie noteikumi vai ietekmes izvērtējums."),
        ("Вероятно, действуют условия DAP", "На природной территории для выбранного действия могут понадобиться технические условия Управления охраны природы (DAP) или оценка воздействия."),
        ("DAP conditions likely", "In a nature area the selected action may need technical conditions or an impact assessment from the Nature Conservation Agency (DAP)."),
    ),
    "MUNICIPAL_APPROVAL_LIKELY": (
        ("Visticamāk vajadzīga pašvaldības saskaņošana", "Izvēlētās darbības novietojums jāsaskaņo ar pašvaldību (būvvaldi vai reklāmas saskaņošanas speciālistu)."),
        ("Вероятно, нужно согласование самоуправления", "Размещение выбранного объекта нужно согласовать с самоуправлением (стройуправой или специалистом по рекламе)."),
        ("Municipal approval likely", "The placement has to be agreed with the municipality (building board or advertising officer)."),
    ),
}

RESULT_LEVELS = {
    "green": (("Zaļš", "Atvērtajos datos ierobežojumi izvēlētajai darbībai nav atrasti."),
              ("Зелёный", "В открытых данных ограничений для выбранного действия не найдено."),
              ("Green", "No restriction for the selected action was found in open data.")),
    "yellow": (("Dzeltens", "Atrasts ierobežojums, kura dēļ visticamāk vajadzīga saskaņošana."),
               ("Жёлтый", "Найдено ограничение, из-за которого, вероятно, нужно согласование."),
               ("Yellow", "A restriction was found that most likely requires an approval.")),
    "red": (("Sarkans", "Pārbaudi nevarēja veikt (piemēram, adrese nav atrasta) - nepieciešama precizēšana."),
            ("Красный", "Проверку выполнить не удалось (например, адрес не найден) - нужно уточнение."),
            ("Red", "The check could not be completed (e.g. the address was not found).")),
}

# Short fallback definitions + translations for Riga TIAN zone families.
ZONE_FAMILIES = {
    "DzS": ("Savrupmāju apbūves teritorija", "Территория застройки особняками (частные дома)", "Detached-house residential area"),
    "DzM": ("Mazstāvu dzīvojamās apbūves teritorija", "Территория малоэтажной жилой застройки", "Low-rise residential area"),
    "DzD": ("Daudzstāvu dzīvojamās apbūves teritorija", "Территория многоэтажной жилой застройки", "Multi-storey residential area"),
    "JC": ("Jauktas centra apbūves teritorija", "Территория смешанной застройки центра", "Mixed-use centre area"),
    "R": ("Rūpnieciskās apbūves teritorija", "Территория промышленной застройки", "Industrial area"),
    "TR": ("Transporta infrastruktūras teritorija", "Территория транспортной инфраструктуры", "Transport infrastructure area"),
    "TA": ("Tehniskās apbūves teritorija", "Территория технической застройки", "Technical infrastructure area"),
    "DA": ("Dabas un apstādījumu teritorija", "Территория природы и зелёных насаждений", "Nature and green area"),
    "Ū": ("Ūdeņu teritorija", "Водная территория", "Water area"),
    "P": ("Publiskās apbūves teritorija", "Территория общественной застройки", "Public building area"),
    "L": ("Lauksaimniecības teritorija", "Сельскохозяйственная территория", "Agricultural area"),
    "M": ("Mežu teritorija", "Лесная территория", "Forest area"),
    "A": ("Apstādījumu teritorija", "Территория зелёных насаждений", "Green area"),
    "TIN": ("Teritorija ar īpašiem noteikumiem", "Территория с особыми правилами", "Territory with special rules"),
}

NATURE_CATEGORIES = {
    "DL": "Dabas liegums", "DP": "Dabas parks", "NP": "Nacionālais parks", "DR": "Dabas rezervāts",
    "AAA": "Aizsargājamo ainavu apvidus", "AJT": "Aizsargājama jūras teritorija", "ML": "Mikroliegums",
    "BR": "Biosfēras rezervāts", "DPie:ĢĢ": "Ģeoloģisks un ģeomorfoloģisks dabas piemineklis",
    "DPie:DS": "Dendroloģiskie stādījumi (dabas piemineklis)", "DPie:DA": "Aizsargājama aleja (dabas piemineklis)",
    "Dpie:cits": "Cits dabas piemineklis",
}
NATURE_ZONES = {
    "DLZ": "Dabas lieguma zona", "RRZ": "Regulējamā režīma zona", "NZ": "Neitrālā zona", "DPZ": "Dabas parka zona",
    "AAZ": "Ainavu aizsardzības zona", "SLZ": "Sezonas lieguma zona", "SRZ": "Stingrā režīma zona",
    "KvZ": "Kultūrvēsturiskā zona", "cita": "Cita zona",
}

LAWS = {
    "22-buvniecibas-likums.html": ("Būvniecības likums", "pants"),
    "23-eku-buvnoteikumi-mk-529.html": ("MK noteikumi Nr. 529 \"Ēku būvnoteikumi\"", "punkts"),
    "24-visparigie-buvnoteikumi-mk-500.html": ("MK noteikumi Nr. 500 \"Vispārīgie būvnoteikumi\"", "punkts"),
    "26-aizsargjoslu-likums.html": ("Aizsargjoslu likums", "pants"),
    "27-kulturas-piemineklu-aizsardziba.html": ("Likums \"Par kultūras pieminekļu aizsardzību\"", "pants"),
    "28-ipasi-aizsargajamo-dabas-teritoriju-noteikumi.html": ("MK noteikumi Nr. 264 \"Īpaši aizsargājamo dabas teritoriju vispārējie aizsardzības un izmantošanas noteikumi\"", "punkts"),
}

TIAN_URL = "https://www.rdpad.lv/rtp/"
TAPIS_URL = "https://geolatvija.lv/geo/tapis"


def label(kind, code, title, text, source, url=None, aliases=None, meta=None, ru=None, en=None):
    item = {"kind": kind, "code": code, "title": title, "text": text, "source": source}
    if url:
        item["url"] = url
    if aliases:
        item["aliases"] = sorted({a for a in aliases if a and a != code})
    if meta:
        item["meta"] = meta
    translations = {}
    if ru:
        translations["ru"] = {"title": ru[0], "text": ru[1]}
    if en:
        translations["en"] = {"title": en[0], "text": en[1]}
    if translations:
        item["translations"] = translations
    return item


def clean(text: str | None) -> str:
    return re.sub(r"\s+", " ", (text or "").replace("\xa0", " ")).strip()


# ---------------------------------------------------------------------------
# Builders
# ---------------------------------------------------------------------------

def build_reason_labels():
    out = []
    for code, (lv, ru, en) in REASON_CODES.items():
        out.append(label("reason", code, lv[0], lv[1], "Vai drīkstu te darīt? noteikumu dzinējs", ru=ru, en=en))
    for code, (lv, ru, en) in RESULT_LEVELS.items():
        out.append(label("level", code, lv[0], lv[1], "Vai drīkstu te darīt? noteikumu dzinējs", ru=ru, en=en))
    return out


def build_encumbrances(sources: Path, riga_groups: dict, extra: dict):
    codes: dict[str, dict] = {}
    files = sorted(sources.glob("apgrutinajumi_zemes_vienib*_*.csv"))
    for path in files:
        region = path.stem.split("_")[-2]
        part = "zemes vienības daļa" if "_dalas_" in path.name else "zemes vienība"
        with open(path, encoding="utf-8-sig", newline="") as handle:
            for row in csv.DictReader(handle):
                code = row["ENCD_ID"].strip()
                item = codes.setdefault(code, {"text": clean(row["DESCRIPTION"]), "count": 0, "regions": collections.Counter(), "parts": collections.Counter()})
                item["count"] += 1
                item["regions"][region] += 1
                item["parts"][part] += 1
    for code, text in extra.items():
        codes.setdefault(code, {"text": text, "count": 0, "regions": collections.Counter(), "parts": collections.Counter()})

    out = []
    for code, item in sorted(codes.items()):
        group = None
        for prefix_len in (8, 6, 4):
            key = code[:prefix_len].ljust(10, "0")
            if key in riga_groups and key != code:
                group = riga_groups[key]
                break
        text = item["text"][:1].upper() + item["text"][1:]
        details = []
        if item["count"]:
            details.append(f"VZD datos (31.01.2025) reģistrēts {item['count']:,} reizes".replace(",", " "))
        meta = {
            "count": item["count"],
            "regions": dict(item["regions"].most_common()),
        }
        if group:
            meta["group"] = group
        legacy = len(code) < 10
        if legacy:
            meta["legacy"] = True
        out.append(label("encumbrance", code, text, text, "VZD Kadastra informācijas sistēma: zemes vienību apgrūtinājumi", meta=meta))
    return out, files


def build_marks(sources: Path):
    archive = sources / "data" / "mark.zip"
    types: dict[str, dict] = {}
    by_cad: dict[str, list] = {}
    if not archive.exists():
        return [], {}
    pattern = re.compile(r"<MarkItemData><ObjectRelation><ObjectCadastreNr>(.*?)</ObjectCadastreNr><ObjectType>(.*?)</ObjectType></ObjectRelation><MarkList>(.*?)</MarkList></MarkItemData>", re.S)
    rec = re.compile(r"<MarkType>(.*?)</MarkType>(?:<MarkDate>(.*?)</MarkDate>)?<MarkDescription>(.*?)</MarkDescription>", re.S)
    prepared = None
    with zipfile.ZipFile(archive) as zf:
        for name in zf.namelist():
            if not name.endswith(".xml"):
                continue
            text = zf.read(name).decode("utf-8")
            prepared = prepared or (re.search(r"<PreparedDate>(.*?)</PreparedDate>", text) or [None, None])[1]
            for cad, obj, marks in pattern.findall(text):
                entry = by_cad.setdefault(cad, {"t": obj, "m": []})
                for mtype, mdate, desc in rec.findall(marks):
                    info = types.setdefault(mtype, {"text": clean(desc), "count": 0, "objects": collections.Counter()})
                    info["count"] += 1
                    info["objects"][obj] += 1
                    entry["m"].append([mtype, mdate or None])

    def family(code: str) -> str | None:
        if len(code) == 4 and code[:2] in {"10", "11", "12"}:
            return "Publiskais ezers vai ūdenskrātuve (Civillikuma 1. pielikums)"
        if len(code) == 4 and code.startswith("20"):
            return "Publiskā upe (Civillikuma 2. pielikums)"
        if len(code) == 4 and code.startswith("30"):
            return "Jūras piekraste"
        return None

    out = []
    for code, info in sorted(types.items()):
        meta = {"count": info["count"], "objects": dict(info["objects"])}
        fam = family(code)
        if fam:
            meta["group"] = fam
        out.append(label("mark", code, info["text"], info["text"] + (f" ({fam})" if fam else ""),
                         f"VZD Kadastra informācijas sistēma: atzīmes (sagatavots {prepared})", meta=meta))
    return out, by_cad


def read_gpkg(path: Path, sql: str):
    if not path.exists():
        return []
    db = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    try:
        return db.execute(sql).fetchall()
    finally:
        db.close()


def tian_text(external: Path) -> str:
    pdf = external / "04-regulations" / "25-rigas-teritorijas-noteikumi-current.pdf"
    if not pdf.exists() or not shutil.which("pdftotext"):
        return ""
    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp) / "tian.txt"
        subprocess.run(["pdftotext", str(pdf), str(out)], check=True)
        return out.read_text(encoding="utf-8", errors="replace")


def build_zones(sources: Path, external: Path):
    labels, documents = [], []
    raw = tian_text(external)
    flat = re.sub(r"\s+", " ", raw.replace("\xa0", " "))

    sections = {}
    if flat:
        heads = list(re.finditer(r"(\d+(?:\.\d+){2,3})\. ([A-ZĀČĒĢĪĶĻŅŠŪŽ][^().]{4,90}?) \(([A-ZĀČĒŪŽa-zāēū]{1,3}\d{0,2})\)(?= \d|\s+\d{3,4}\.)", flat))
        for idx, head in enumerate(heads):
            code = head.group(3)
            if code in sections:
                continue
            end = heads[idx + 1].start() if idx + 1 < len(heads) else head.start() + 6000
            body = flat[head.start(): min(end, head.start() + 6000)]
            sections[code] = (head.group(1), head.group(2), body)

    zone_rows = read_gpkg(sources / "downloaded/rigas-teritorijas-planojums/extracted/funkcionalais_zonejums.gpkg",
                          'select Planotais_, "Stāvi", Vairak_inf, count(*) from funkcionalais_zonejums group by 1, 2, 3')
    per_zone = collections.defaultdict(lambda: {"count": 0, "floors": collections.Counter(), "url": None})
    for code, floors, url, count in zone_rows:
        per_zone[code]["count"] += count
        per_zone[code]["floors"][clean(floors)] += count
        per_zone[code]["url"] = per_zone[code]["url"] or url

    for code in sorted(set(per_zone) | set(sections)):
        if not re.match(r"^(DzS|DzM|DzD|JC|R|TR|TA|DA|Ū)\d*$", code):
            continue
        fam = re.match(r"^[A-ZĀČĒŪŽ][a-zāēū]?[A-Z]?", code).group(0)
        fam = fam if fam in ZONE_FAMILIES else re.sub(r"\d", "", code)
        names = ZONE_FAMILIES.get(fam, (code, code, code))
        title = f"{names[0]} ({code})"
        parts = []
        definition = None
        if code in sections:
            sec_no, sec_name, body = sections[code]
            m = re.search(r"ir funkcionālā zona,? (.{20,500}?)\.\s", body)
            if m:
                definition = f"{sec_name} ({code}) ir funkcionālā zona, {m.group(1)}."
            documents.append({
                "id": f"tian-{code}", "kind": "regulation", "title": f"Rīgas TIAN {sec_no}. apakšnodaļa: {sec_name} ({code})",
                "ref": f"Rīgas teritorijas izmantošanas un apbūves noteikumi, {sec_no}.", "text": body,
                "source": "Rīgas teritorijas plānojums (TIAN, konsolidētā redakcija)", "url": TIAN_URL,
                "codes": [code]})
        parts.append(definition or f"Funkcionālā zona “{names[0]}” Rīgas teritorijas plānojumā.")
        info = per_zone.get(code)
        meta = {}
        if info and info["count"]:
            floors = info["floors"].most_common(1)[0][0]
            parts.append(f"Maksimālais stāvu skaits / augstums: {floors}.")
            meta = {"polygons": info["count"], "maxFloors": floors}
        ru_title = f"{names[1]} ({code})"
        en_title = f"{names[2]} ({code})"
        labels.append(label("zone", code, title, " ".join(parts),
                            "Rīgas teritorijas plānojums: funkcionālais zonējums", url=(info or {}).get("url") or TIAN_URL,
                            meta=meta, ru=(ru_title, f"{names[1]}. Подробности - в оригинале на латышском ниже."),
                            en=(en_title, f"{names[2]}. Details in the Latvian original below.")))

    # Riga historic centre (RVC) planning codes - different document, same letters!
    rvc_rows = read_gpkg(sources / "downloaded/rigas-vesturiskais-centrs/extracted/Planota_atlauta_izmantosana.gpkg",
                         "select trim(Kods), Izm_veids, Izm_v_izv, count(*) from planota_atlauta_izmantosana where trim(Kods) <> '' group by 1, 2, 3")
    rvc = collections.defaultdict(lambda: {"names": collections.Counter(), "count": 0})
    for code, kind, detail, count in rvc_rows:
        rvc[code]["names"][(clean(kind), clean(detail))] += count
        rvc[code]["count"] += count
    for code, info in sorted(rvc.items()):
        (kind, detail), _ = info["names"].most_common(1)[0]
        text = detail if detail and detail != "Nenosaka" else kind
        labels.append(label("zone-rvc", code, f"{kind} ({code}) - Rīgas vēsturiskais centrs", text,
                            "Rīgas vēsturiskā centra un tā aizsardzības zonas teritorijas plānojums: plānotā (atļautā) izmantošana",
                            meta={"polygons": info["count"]}))

    # Territories with special rules (TIN)
    for veids, count in read_gpkg(sources / "downloaded/rigas-teritorijas-planojums/extracted/Teritorijas_ar_ipasiem_noteikumiem.gpkg",
                                  "select Veids, count(*) from teritorijas_ar_ipasiem_noteikumiem group by 1"):
        m = re.search(r"\((TIN\d+)\)", veids or "")
        if not m:
            continue
        code = m.group(1)
        name = clean(veids.replace(f"({code})", ""))
        labels.append(label("tin", code, f"{name} ({code})", f"{name} - teritorija ar īpašiem noteikumiem Rīgas teritorijas plānojumā.",
                            "Rīgas teritorijas plānojums: teritorijas ar īpašiem noteikumiem", url=TIAN_URL,
                            meta={"polygons": count},
                            ru=(f"Территория с особыми правилами ({code})", f"{name}. Территория с особыми правилами в плане Риги."),
                            en=(f"Territory with special rules ({code})", f"{name}. Special-rule territory in the Riga plan.")))

    # Generic TAPIS index letters (whole Latvia classification)
    tapis = sources / "downloaded/tapis/tapis_funkcionalais_zonejums_riga_bbox.geojson"
    if tapis.exists():
        seen = collections.Counter()
        names = {}
        data = json.loads(tapis.read_text(encoding="utf-8"))
        for feature in data.get("features", []):
            p = feature.get("properties", {})
            idx = p.get("indekss")
            if idx and not re.search(r"\d", idx):
                seen[idx] += 1
                names.setdefault(idx, (p.get("veids"), p.get("kods"), p.get("grupa_nos")))
        for idx, count in sorted(seen.items()):
            veids, kods, grupa = names[idx]
            fam = ZONE_FAMILIES.get(idx)
            labels.append(label("zone-tapis", idx, f"{veids} ({idx})",
                                f"{veids} - funkcionālās zonas indekss TAPIS (teritorijas attīstības plānošanas informācijas sistēmā). Klasifikatora kods {kods}.",
                                "TAPIS funkcionālais zonējums (WFS)", url=TAPIS_URL, meta={"classifier": kods, "features": count},
                                ru=(f"{fam[1] if fam else veids} ({idx})", "Индекс функциональной зоны в TAPIS.") if fam else None,
                                en=(f"{fam[2] if fam else veids} ({idx})", "Functional zone index in TAPIS.") if fam else None))
    return labels, documents


def build_protection_zones(external: Path):
    folder = external / "02-additional-geodata" / "12-riga-protection-zones-arcgis"
    meta_path = folder / "service-metadata.json"
    out = []
    if not meta_path.exists():
        return out
    service = json.loads(meta_path.read_text(encoding="utf-8"))
    for layer in service.get("layers", []):
        layer_id = layer["id"]
        name = clean(layer["name"])
        codes = set()
        for geo in folder.glob(f"layer-{layer_id:02d}-*.geojson"):
            try:
                data = json.loads(geo.read_text(encoding="utf-8"))
            except Exception:
                continue
            for feature in data.get("features", [])[:5000]:
                props = feature.get("properties") or {}
                for key in ("kods", "KODS", "Kods"):
                    if props.get(key):
                        codes.add(str(props[key]))
        meta = {"layerId": layer_id, "geometry": layer.get("geometryType", "").replace("esriGeometry", "")}
        if codes:
            meta["encumbranceCodes"] = sorted(codes)[:20]
        out.append(label("protection-zone", f"AZ-{layer_id}", name,
                         f"{name}. Rīgas pašvaldības aizsargjoslu un citu teritorijas izmantošanas aprobežojumu slānis (ArcGIS FeatureServer 'Aizsargjoslas', izgūts GeoJSON formātā).",
                         "Rīgas valstspilsētas pašvaldība: aizsargjoslas (ArcGIS)", meta=meta, aliases=sorted(codes)[:20]))
    return out


def read_dbf(path: Path):
    with open(path, "rb") as handle:
        header = handle.read(32)
        count, header_len, record_len = struct.unpack("<IHH", header[4:12])
        fields = []
        while True:
            desc = handle.read(32)
            if desc[0] == 0x0D:
                break
            fields.append((desc[:11].split(b"\0")[0].decode(), desc[16]))
        handle.seek(header_len)
        rows = []
        for _ in range(count):
            rec = handle.read(record_len)
            if not rec or rec[:1] == b"*":
                continue
            pos, row = 1, {}
            for name, length in fields:
                row[name] = rec[pos:pos + length].decode("utf-8", "replace").strip()
                pos += length
            rows.append(row)
        return rows


def build_nature(sources: Path):
    out, atis = [], {}
    folder = sources / "Ipasi_aizsargajamas_dabas_teritorijas"
    base = folder / "IADT_pamatteritorijas.dbf"
    zones = folder / "IADT_zonejums.dbf"
    for code, name in NATURE_CATEGORIES.items():
        out.append(label("nature-category", code, name, f"{name} - īpaši aizsargājamās dabas teritorijas (ĪADT) kategorija.",
                         "DAP: īpaši aizsargājamās dabas teritorijas"))
    for code, name in NATURE_ZONES.items():
        out.append(label("nature-zone", code, name, f"{name} - funkcionālā zona īpaši aizsargājamā dabas teritorijā.",
                         "DAP: ĪADT funkcionālais zonējums"))
    if base.exists():
        zone_names = collections.defaultdict(set)
        if zones.exists():
            for row in read_dbf(zones):
                zone_names[row["SITE_NAME"]].add(NATURE_ZONES.get(row["NAME"], row["NAME"]))
                if row["ATIS_CODE"]:
                    atis[row["ATIS_CODE"]] = row["ATIS_veids"]
        for row in read_dbf(base):
            if row["ATIS_CODE"]:
                atis[row["ATIS_CODE"]] = row["ATIS_veids"]
            category = NATURE_CATEGORIES.get(row["CATEGORY"], row["CATEGORY"])
            parts = [f"{row['NAME']} - {category.lower()}."]
            if row["SITE_CODE"]:
                parts.append(f"Kods {row['SITE_CODE']}.")
            if row["NATURA_STA"] in ("1", "2"):
                parts.append("Iekļauta Natura 2000 tīklā." if row["NATURA_STA"] == "1" else "")
            if zone_names.get(row["NAME"]):
                parts.append("Funkcionālās zonas: " + ", ".join(sorted(zone_names[row["NAME"]])) + ".")
            if row["ATIS_veids"]:
                parts.append(f"Apgrūtinājums: {row['ATIS_veids']} ({row['ATIS_CODE']}).")
            try:
                area = float(row["AREA_DOC"])
                parts.append(f"Platība dokumentā: {area:,.1f} ha.".replace(",", " "))
            except ValueError:
                pass
            out.append(label("nature-area", row["SITE_CODE"] or f"IADT-{row['ID']}", f"{row['NAME']} ({category})",
                             " ".join(p for p in parts if p), "DAP: īpaši aizsargājamās dabas teritorijas (pamatteritorijas)",
                             url=row["LEGAL_ACT"] or None, aliases=[row["NAME"]],
                             meta={"category": row["CATEGORY"], "encumbranceCode": row["ATIS_CODE"] or None}))
    return out, atis


def build_monuments(sources: Path):
    path = sources / "saraksts.csv"
    out = []
    if not path.exists():
        return out
    with open(path, encoding="utf-8-sig", newline="") as handle:
        for row in csv.DictReader(handle, delimiter=";"):
            cads = [c.strip() for c in (row.get("Kadastra_apzimejums") or "").split(";") if re.fullmatch(r"\d{11,14}", c.strip())]
            out.append({
                "nr": row["Objekta_Nr"].strip(),
                "name": clean(row["Nosaukums"]),
                "group": clean(row["Tipologiska_grupa"]),
                "value": clean(row["Vertibas_grupa"]),
                "dating": clean(row["Datejums"]),
                "location": clean(row["Atrasanas_vieta"]),
                "since": clean(row["Stajies_speka"]),
                "condition": clean(row["Saglabatibas_stavoklis"]),
                "access": clean(row["Publiska_pieejamiba"]),
                "movable": clean(row["Veids"]) == "Kustams",
                "encumbranceCode": clean(row["Apgrutinajuma_kods"]) or None,
                "cadastre": cads,
            })
    return out


def build_laws(external: Path):
    try:
        from bs4 import BeautifulSoup
    except ImportError:  # pragma: no cover
        print("beautifulsoup4 is missing: pip install beautifulsoup4 lxml", file=sys.stderr)
        return []
    docs = []
    folder = external / "04-regulations"
    for filename, (title, unit) in LAWS.items():
        path = folder / filename
        if not path.exists():
            continue
        soup = BeautifulSoup(path.read_text(encoding="utf-8"), "html.parser")
        canonical = soup.find("link", rel="canonical")
        url = canonical["href"] if canonical and canonical.get("href") else None
        for div in soup.select("div.TV213[data-num]"):
            for junk in div.select(".panta-piezimite, .labojumu_pamats, .info-icon-wrapper, .court"):
                junk.decompose()
            text = clean(div.get_text(" ", strip=True))
            if len(text) < 20:
                continue
            num = div["data-num"].replace("_", ".")
            ref = f"{num}. {unit}"
            docs.append({"id": f"{Path(filename).stem}-{div['data-num']}", "kind": "regulation", "title": f"{title}, {ref}",
                         "ref": ref, "text": text[:6000], "source": title, "url": url})
    return docs


def build_datasets(sources: Path, external: Path):
    docs, columns = [], []

    def add(doc_id, title, text, source, url=None):
        docs.append({"id": doc_id, "kind": "dataset", "title": title, "ref": "Datu kopa", "text": clean(text)[:4000], "source": source, "url": url})

    for path in sorted(external.glob("02-additional-geodata/*/package-metadata.json")) + sorted(external.glob("05-services/*/package-metadata.json")):
        meta = json.loads(path.read_text(encoding="utf-8"))
        org = (meta.get("organization") or {}).get("title") if isinstance(meta.get("organization"), dict) else None
        add(f"ds-{path.parent.name}", meta.get("title") or path.parent.name,
            f"{meta.get('notes') or ''} Uzturētājs: {org or meta.get('maintainer') or '-'}. Atjaunošana: {(meta.get('frequency') or '').rsplit('/', 1)[-1]}. Licence: {meta.get('license_title') or '-'}. Mape: {path.parent.name}.",
            "data.gov.lv katalogs", f"https://data.gov.lv/dati/dataset/{meta.get('name')}" if meta.get("name") else None)

    bis = external / "03-bis" / "bis-catalog-metadata.json"
    if bis.exists():
        for meta in json.loads(bis.read_text(encoding="utf-8")).get("results", []):
            add(f"ds-{meta['name']}", meta.get("title"), f"{meta.get('notes') or ''} Uzturētājs: {meta.get('maintainer') or '-'} (BIS, Būvniecības valsts kontroles birojs). Mape: 03-bis/{meta['name']}.",
                "data.gov.lv katalogs (BIS)", f"https://data.gov.lv/dati/dataset/{meta['name']}")

    for path in sorted((sources / "data").glob("aw_*_metadata.json")):
        meta = json.loads(path.read_text(encoding="utf-8"))
        table = path.name.replace("_metadata.json", "")
        cols = meta.get("tableSchema", {}).get("columns", [])
        add(f"ds-{table}", f"Valsts adrešu reģistrs: {table}.csv",
            f"{meta.get('dc:title', '')}. Tabula {table}. Kolonnas: " + ", ".join(f"{c.get('name')} ({c.get('titles')})" for c in cols),
            "VZD Valsts adrešu reģistrs", meta.get("url"))
        for col in cols:
            desc = clean(col.get("dc:description"))
            if not desc:
                continue
            columns.append(label("address-column", f"{table}.{col.get('name')}", f"{col.get('titles')} ({col.get('name')}, {table}.csv)", desc,
                                 "VZD Valsts adrešu reģistra atvērto datu apraksts", url=meta.get("url"), aliases=[col.get("name")]))

    static = [
        ("ds-apgrutinajumi", "Zemes vienību un to daļu apgrūtinājumi (apgrutinajumi_*.csv)",
         "VZD Kadastra informācijas sistēmas izgūtie zemes vienību (un zemes vienību daļu) apgrūtinājumi pa plānošanas reģioniem (Kurzeme, Latgale, Rīga, Vidzeme, Zemgale) uz 31.01.2025. Kolonnas: ADMRKIND (novads/pilsēta), ADMTKIND (teritorija), CAD_NR (zemes vienības kadastra apzīmējums), ENCD_ID (apgrūtinājuma kods), DESCRIPTION (apgrūtinājuma veids), NR, ENC_DATE (reģistrācijas datums), ENC_SIZE, MEASURE (platība, mērvienība)."),
        ("ds-kk-shp", "Kadastra karte (NNNNNNN_kk_shp.zip)", "VZD kadastra kartes telpiskie dati (zemes vienības, būves, apgrūtinājumu robežas) shapefile formātā pa administratīvajām teritorijām; faila nosaukuma sākums ir ATVK kods (piem. 0001000 - Rīga)."),
        ("ds-mark", "Kadastra objektu atzīmes (mark.zip)", "VZD Kadastra informācijas sistēmas atzīmes kadastra objektiem (zemes vienībām, būvēm, īpašumiem): piemēram, starpgabals, koordinātu nesakritība, atsavināšanas aizliegums, hipotēka, publiskie ezeri un upes."),
        ("ds-encumbrance", "Apgrūtinājumi XML (encumbrance.zip)", "VZD Kadastra informācijas sistēmas pilnie apgrūtinājumu dati XML formātā pa ATVK."),
        ("ds-parcel", "Zemes vienības, būves, telpu grupas, īpašumi (parcel/property/premisegroup/ownership.zip)", "VZD Kadastra informācijas sistēmas atvērtie dati XML formātā: zemes vienības (parcel), zemes vienību daļas (parcelpart), nekustamie īpašumi (property), telpu grupas (premisegroup), īpašumtiesību veidi (ownership)."),
        ("ds-saraksts", "Valsts aizsargājamo nekustamo kultūras pieminekļu saraksts (saraksts.csv)", "Nacionālās kultūras mantojuma pārvaldes saraksts: objekta numurs, nosaukums, tipoloģiskā grupa, vērtības grupa (valsts / reģiona / vietējās nozīmes), datējums, atrašanās vieta, apgrūtinājuma kods, kadastra apzīmējumi."),
        ("ds-iadt", "Īpaši aizsargājamās dabas teritorijas (IADT_*.shp)", "Dabas aizsardzības pārvaldes ĪADT pamatteritorijas (dabas liegumi, parki, nacionālie parki, dabas pieminekļi, mikroliegumi) un to funkcionālais zonējums."),
        ("ds-pludi", "Plūdu riska informācija (pludi/*.xml)", "Lejupielādētie plūdu riska slāņu metadati (pludu_risk_*.xml)."),
        ("ds-regulations", "Normatīvie akti (04-regulations)", "Būvniecības likums, MK noteikumi Nr. 529 un Nr. 500, Rīgas TIAN, Aizsargjoslu likums, likums 'Par kultūras pieminekļu aizsardzību', MK noteikumi Nr. 264 par ĪADT. Snapshot no likumi.lv; pirms lietošanas pārbaudiet aktuālo redakciju."),
    ]
    for doc_id, title, text in static:
        add(doc_id, title, text, "Projekta datu apraksts")
    return docs, columns


def riga_encumbrance_groups(sources: Path):
    rows = read_gpkg(sources / "downloaded/rigas-teritorijas-planojums/extracted/apgrutinatas_teritorijas.gpkg",
                     "select grupa_kods, grupa_nos, kods, veids from apgrutinatas_teritorijas")
    groups, extra = {}, {}
    for gk, gn, code, veids in rows:
        if gk and gn:
            groups[gk] = clean(gn)
        if code and veids:
            extra[code] = clean(veids)
    return groups, extra


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--sources", required=True, type=Path, help="folder 'sources' (with apgrutinajumi_*.csv, data/, downloaded/)")
    parser.add_argument("--external", required=True, type=Path, help="folder 'data/external' (with 02-additional-geodata, 03-bis, 04-regulations)")
    parser.add_argument("--out", default=Path("data/assistant"), type=Path)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)

    groups, extra = riga_encumbrance_groups(args.sources)
    nature, atis = build_nature(args.sources)
    extra.update({k: v for k, v in atis.items() if k not in extra})
    encumbrances, enc_files = build_encumbrances(args.sources, groups, extra)
    marks, by_cad = build_marks(args.sources)
    zones, zone_docs = build_zones(args.sources, args.external)
    datasets, columns = build_datasets(args.sources, args.external)
    labels = build_reason_labels() + encumbrances + marks + zones + build_protection_zones(args.external) + nature + columns
    documents = zone_docs + build_laws(args.external) + datasets
    monuments = build_monuments(args.sources)

    generated = datetime.now(timezone.utc).isoformat(timespec="seconds")
    write = lambda name, payload: (args.out / name).write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    write("labels.json", {"generatedAt": generated, "items": labels})
    write("documents.json", {"generatedAt": generated, "items": documents})
    write("monuments.json", {"generatedAt": generated, "items": monuments})
    write("cadastre-marks.json", {"generatedAt": generated, "items": by_cad})

    stats = collections.Counter(item["kind"] for item in labels)
    stats.update(f"doc:{d['kind']}" for d in documents)
    stats["monuments"] = len(monuments)
    stats["cadastre-objects-with-marks"] = len(by_cad)
    stats["encumbrance-source-files"] = len(enc_files)
    write("manifest.json", {"generatedAt": generated, "counts": dict(sorted(stats.items()))})
    for key, value in sorted(stats.items()):
        print(f"{key:32} {value}")


if __name__ == "__main__":
    main()
