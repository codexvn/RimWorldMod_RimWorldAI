# 语义字符映射表 — 供 AI 直接编辑 Symbols.json 后校验
# 每个字符只出现一次，目标是与 def 语义匹配

MAP = {
    # 地形
    "Soil": ",",  "SoilRich": ":",  "Gravel": "`",    "Sand": '"',
    "WaterShallow": "~", "WaterDeep": "W", "Mud": "Z", "Ice": "=",
    "Marsh": "M", "Concrete": "_",
    # 建筑
    "Wall": "#", "Door": "D", "Column": "O", "Fence": "/",
    "Sandbags": "n", "TrapSpike": "^", "PowerConduit": "-",
    "Battery": "B", "SolarGenerator": "G", "Heater": "H",
    "Cooler": "C", "Campfire": "*", "StandingLamp": "L",
    "SimpleResearchBench": "Q", "ElectricStove": "K",
    "VitalsMonitor": "+", "CommsConsole": "@", "SculptureSmall": "A",
    "NutrientPasteDispenser": "N", "Turret_MiniTurret": "T",
    "CryptosleepCasket": "X", "Grave": "v",
    # 物品
    "Steel": "S", "Plasteel": "a", "Silver": "$", "Gold": "g",
    "Uranium": "u", "WoodLog": "w", "ComponentIndustrial": "c",
    "Chemfuel": "f", "Neutroamine": "m", "MedicineHerbal": "h",
    "Kibble": "F", "MealSimple": "E", "RawRice": "R",
    "RawCorn": "U", "RawPotatoes": "I", "RawBerries": "b",
    # 植物
    "Plant_Rice": "r", "Plant_Potato": "p", "Plant_Healroot": "z",
    "Plant_Cotton": "t", "Plant_Hops": "j", "Plant_Smokeleaf": "k",
    "Plant_Psychoid": "y", "Plant_Devilstrand": "d",
    "Plant_TreeOak": "Y", "Plant_Grass": "i", "Plant_Bush": "V",
    "Plant_Rose": "J", "Plant_Daylily": "l",
}
