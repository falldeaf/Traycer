import json, time
pipe = r'\\.\pipe\TraycerHud'
def send(obj):
    with open(pipe, 'w', encoding='utf-8', newline='\n') as f:
        f.write(json.dumps(obj, ensure_ascii=False) + '\n')

send({"op":"set","well":"weather","text":"🌦️  71°F Light rain"})
