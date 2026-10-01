-- ----------------------------------------------------------------------------
-- Stock -> Sector mappings (stock_sectors)
-- Run after schema.sql. Symbols missing from stock_master are skipped.
-- ----------------------------------------------------------------------------

-- NIFTY AUTO
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY AUTO'
WHERE s.symbol IN (
    'HYUNDAI', 'HEROMOTOCO', 'TIINDIA', 'TMPV', 'ASHOKLEY',
    'BHARATFORG', 'EICHERMOT', 'TVSMOTOR', 'MOTHERSON', 'MARUTI',
    'M&M', 'SONACOMS', 'BOSCHLTD', 'UNOMINDA', 'BAJAJ-AUTO'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY BANK
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY BANK'
WHERE s.symbol IN (
    'HDFCBANK', 'KOTAKBANK', 'BANKBARODA', 'FEDERALBNK', 'AXISBANK',
    'YESBANK', 'SBIN', 'ICICIBANK', 'CANBK', 'INDUSINDBK',
    'IDFCFIRSTB', 'PNB', 'UNIONBANK', 'AUBANK'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY FINANCIAL SERVICES
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY FINANCIAL SERVICES'
WHERE s.symbol IN (
    'HDFCLIFE', 'MFSL', 'HDFCBANK', 'KOTAKBANK', 'SBILIFE',
    'AXISBANK', 'BAJAJFINSV', 'SBIN', 'ICICIBANK', 'MUTHOOTFIN',
    'ICICIGI', 'BAJFINANCE', 'BSE', 'CHOLAFIN', 'PFC',
    'JIOFIN', 'SBICARD', 'RECLTD', 'SHRIRAMFIN', 'LICHSGFIN'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY FINANCIAL SERVICES 25/50
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY FINANCIAL SERVICES 25/50'
WHERE s.symbol IN (
    'HDFCLIFE', 'MFSL', 'HDFCBANK', 'KOTAKBANK', 'SBILIFE',
    'AXISBANK', 'BAJAJFINSV', 'SBIN', 'ICICIBANK', 'MUTHOOTFIN',
    'ICICIGI', 'BAJFINANCE', 'BSE', 'CHOLAFIN', 'PFC',
    'SBICARD', 'JIOFIN', 'RECLTD', 'SHRIRAMFIN', 'LICHSGFIN'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY FMCG
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY FMCG'
WHERE s.symbol IN (
    'TATACONSUM', 'NESTLEIND', 'UBL', 'DABUR', 'GODREJCP',
    'BRITANNIA', 'MARICO', 'COLPAL', 'UNITDSPR', 'GODFRYPHLP',
    'RADICO', 'PATANJALI', 'HINDUNILVR', 'ITC', 'VBL'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY IT
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY IT'
WHERE s.symbol IN (
    'MPHASIS', 'COFORGE', 'INFY', 'PERSISTENT', 'HCLTECH',
    'TCS', 'TECHM', 'WIPRO', 'LTM', 'OFSS'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY METAL
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY METAL'
WHERE s.symbol IN (
    'HINDALCO', 'APLAPOLLO', 'NMDC', 'NATIONALUM', 'JSL',
    'JSWSTEEL', 'VAML', 'HINDZINC', 'ADANIENT', 'TATASTEEL',
    'SAIL', 'VEDL', 'JINDALSTEL', 'HINDCOPPER', 'LLOYDSME'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY PHARMA
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY PHARMA'
WHERE s.symbol IN (
    'IPCALAB', 'MANKIND', 'CIPLA', 'SAILIFE', 'ALKEM',
    'AUROPHARMA', 'LUPIN', 'LAURUSLABS', 'TORNTPHARM', 'SUNPHARMA',
    'BIOCON', 'GLAND', 'ABBOTINDIA', 'GLENMARK', 'DIVISLAB',
    'AJANTPHARM', 'DRREDDY', 'ZYDUSLIFE', 'WOCKPHARMA', 'PPLPHARMA'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY PSU BANK
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY PSU BANK'
WHERE s.symbol IN (
    'BANKBARODA', 'MAHABANK', 'SBIN', 'INDIANB', 'UCOBANK',
    'CENTRALBK', 'CANBK', 'BANKINDIA', 'PSB', 'IOB',
    'PNB', 'UNIONBANK'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY REALTY
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY REALTY'
WHERE s.symbol IN (
    'PRESTIGE', 'PHOENIXLTD', 'OBEROIRLTY', 'GODREJPROP', 'SOBHA',
    'DLF', 'BRIGADE', 'LODHA', 'ANANTRAJ', 'ABREL'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY PRIVATE BANK
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY PRIVATE BANK'
WHERE s.symbol IN (
    'RBLBANK', 'HDFCBANK', 'KOTAKBANK', 'FEDERALBNK', 'AXISBANK',
    'ICICIBANK', 'YESBANK', 'INDUSINDBK', 'IDFCFIRSTB', 'BANDHANBNK'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY HEALTHCARE INDEX
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY HEALTHCARE INDEX'
WHERE s.symbol IN (
    'IPCALAB', 'FORTIS', 'MAXHEALTH', 'MANKIND', 'CIPLA',
    'ALKEM', 'AUROPHARMA', 'LUPIN', 'TORNTPHARM', 'SUNPHARMA',
    'LAURUSLABS', 'BIOCON', 'APOLLOHOSP', 'ABBOTINDIA', 'GLAND',
    'GLENMARK', 'DIVISLAB', 'DRREDDY', 'ZYDUSLIFE', 'KIMS'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY CONSUMER DURABLES
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY CONSUMER DURABLES'
WHERE s.symbol IN (
    'HAVELLS', 'KAJARIACER', 'LGEINDIA', 'BATAINDIA', 'TITAN',
    'VOLTAS', 'WHIRLPOOL', 'DIXON', 'PGEL', 'CROMPTON',
    'AMBER', 'THANGAMAYL', 'BLUESTARCO', 'KALYANKJIL'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY OIL & GAS
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY OIL & GAS'
WHERE s.symbol IN (
    'PETRONET', 'AEGISLOG', 'GAIL', 'BPCL', 'HINDPETRO',
    'RELIANCE', 'CASTROLIND', 'ONGC', 'IOC', 'OIL',
    'IGL', 'MGL', 'ATGL', 'AEGISVOPAK', 'CHENNPETRO'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY MIDSMALL HEALTHCARE
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY MIDSMALL HEALTHCARE'
WHERE s.symbol IN (
    'ASTERDM', 'IPCALAB', 'LALPATHLAB', 'FORTIS', 'GLAXO',
    'MANKIND', 'LUPIN', 'SAILIFE', 'ALKEM', 'AUROPHARMA',
    'NEULANDLAB', 'NATCOPHARM', 'LAURUSLABS', 'BIOCON', 'COHANCE',
    'GLAND', 'MEDANTA', 'ABBOTINDIA', 'GLENMARK', 'AJANTPHARM',
    'ANTHEM', 'POLYMED', 'SYNGENE', 'GRANULES', 'KIMS',
    'ACUTAAS', 'WOCKPHARMA', 'NH', 'ONESOURCE', 'PPLPHARMA'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY CHEMICALS
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY CHEMICALS'
WHERE s.symbol IN (
    'PIDILITIND', 'COROMANDEL', 'SRF', 'TATACHEM', 'LINDEINDIA',
    'ATUL', 'NAVINFLUOR', 'AARTIIND', 'HSCL', 'CHAMBLFERT',
    'DEEPAKNTR', 'FLUOROCHEM', 'PIIND', 'JUBLINGREA', 'PCBL',
    'SOLARINDS', 'SWANCORP', 'DEEPAKFERT', 'UPL', 'SUMICHEM'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY500 HEALTHCARE
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY500 HEALTHCARE'
WHERE s.symbol IN (
    'IPCALAB', 'ASTERDM', 'LALPATHLAB', 'FORTIS', 'MAXHEALTH',
    'GLAXO', 'MANKIND', 'SAILIFE', 'LUPIN', 'ALKEM',
    'CIPLA', 'NEULANDLAB', 'AUROPHARMA', 'NATCOPHARM', 'RUBICON',
    'SUNPHARMA', 'EMCURE', 'LAURUSLABS', 'TORNTPHARM', 'COHANCE',
    'BIOCON', 'VIJAYA', 'GLAND', 'ABBOTINDIA', 'APOLLOHOSP',
    'MEDANTA', 'CONCORDBIO', 'GLENMARK', 'AJANTPHARM', 'DIVISLAB',
    'ANTHEM', 'DRREDDY', 'POLYMED', 'GRANULES', 'SYNGENE',
    'RAINBOW', 'ZYDUSLIFE', 'KIMS', 'ACUTAAS', 'WOCKPHARMA',
    'CAPLIPOINT', 'NH', 'ONESOURCE', 'PPLPHARMA'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY FINANCIAL SERVICES EX-BANK
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY FINANCIAL SERVICES EX-BANK'
WHERE s.symbol IN (
    'HDFCLIFE', 'MFSL', 'SBILIFE', 'HDFCAMC', 'ICICIPRULI',
    'CAMS', 'LTF', '360ONE', 'BAJAJFINSV', 'ANGELONE',
    'ICICIGI', 'MUTHOOTFIN', 'LICI', 'BAJFINANCE', 'ABCAPITAL',
    'SBICARD', 'BSE', 'CHOLAFIN', 'PAYTM', 'CDSL',
    'MCX', 'PNBHOUSING', 'BAJAJHLDNG', 'JIOFIN', 'IRFC',
    'PFC', 'RECLTD', 'SHRIRAMFIN', 'LICHSGFIN', 'POLICYBZR'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY MIDSMALL FINANCIAL SERVICES
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY MIDSMALL FINANCIAL SERVICES'
WHERE s.symbol IN (
    'MFSL', 'ICICIPRULI', 'FEDERALBNK', 'CAMS', 'LTF',
    '360ONE', 'YESBANK', 'ICICIGI', 'ANGELONE', 'IDFCFIRSTB',
    'LICI', 'INDIANB', 'INDUSINDBK', 'IEX', 'BANKINDIA',
    'AUBANK', 'KFINTECH', 'ABCAPITAL', 'SBICARD', 'MOTILALOFS',
    'PAYTM', 'BANDHANBNK', 'CDSL', 'MCX', 'PNBHOUSING',
    'MANAPPURAM', 'RECLTD', 'NAM-INDIA', 'LICHSGFIN', 'POLICYBZR'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY MIDSMALL IT & TELECOM
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY MIDSMALL IT & TELECOM'
WHERE s.symbol IN (
    'HFCL', 'MPHASIS', 'COFORGE', 'INDUSTOWER', 'ZENSARTECH',
    'IKS', 'SAGILITY', 'PERSISTENT', 'TATAELXSI', 'NETWEB',
    'HEXT', 'LTTS', 'TATATECH', 'TATACOMM', 'KPITTECH'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY CEMENT
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY CEMENT'
WHERE s.symbol IN (
    'DALBHARAT', 'JSWCEMENT', 'ACC', 'BIRLACORPN', 'ULTRACEMCO',
    'JKCEMENT', 'GRASIM', 'RAMCOCEM', 'INDIACEM', 'AMBUJACEM',
    'SHREECEM', 'NUVOCO'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY REITS & REALTY
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY REITS & REALTY'
WHERE s.symbol IN (
    'PRESTIGE', 'MINDSPACE', 'KRT', 'PHOENIXLTD', 'NXST',
    'BIRET', 'OBEROIRLTY', 'BAGMANE', 'EMBASSY', 'DLF',
    'GODREJPROP', 'BRIGADE', 'LODHA', 'ABREL', 'ANANTRAJ'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;

-- NIFTY MEDIA
INSERT INTO stock_sectors (stock_id, sector_id)
SELECT s.id, sec.id
FROM stock_master s
JOIN sectors sec ON sec.name = 'NIFTY MEDIA'
WHERE s.symbol IN (
    'SUNTV', 'DBCORP', 'NETWORK18', 'ZEEL', 'SAREGAMA',
    'PVRINOX', 'TIPSMUSIC', 'MPSLTD', 'NAZARA', 'PFOCUS'
)
ON CONFLICT (stock_id, sector_id) DO NOTHING;
