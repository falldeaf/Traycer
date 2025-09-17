import json
import requests
import sys
import time
from typing import Optional

PIPE_NAME = r"\\.\\pipe\\TraycerHud"
CONNECT_TIMEOUT = 5.0
TARGET_WELL = "weather"
DEFAULT_WIDTH = 120


def send_json(payload: dict) -> bool:
    data = json.dumps(payload, ensure_ascii=False) + "\n"
    deadline = time.time() + CONNECT_TIMEOUT
    last_error = None
    while time.time() < deadline:
        try:
            with open(PIPE_NAME, "w", encoding="utf-8", newline="\n") as pipe:
                pipe.write(data)
            return True
        except (FileNotFoundError, OSError) as exc:
            last_error = exc
            time.sleep(0.1)

    if last_error is not None:
        print(f"Failed to send to Traycer: {last_error}", file=sys.stderr)
    return False


def ensure_well(width: int = DEFAULT_WIDTH) -> None:
    send_json({"op": "add", "well": TARGET_WELL, "width": width})


def send_to_traycer(text: str) -> bool:
    ensure_well()
    return send_json({"op": "set", "well": TARGET_WELL, "text": text})


def get_weather(lat: float, lon: float) -> Optional[str]:
    url = (
        "https://api.open-meteo.com/v1/forecast"
        f"?latitude={lat}&longitude={lon}&current_weather=true"
        "&temperature_unit=fahrenheit"
    )
    try:
        resp = requests.get(url, timeout=10)
        resp.raise_for_status()
    except requests.RequestException as exc:
        print(f"Weather request failed: {exc}", file=sys.stderr)
        return None

    data = resp.json()
    if "current_weather" not in data:
        print("Weather data not found.", file=sys.stderr)
        return None

    current = data["current_weather"]
    temp = round(current.get("temperature", 0))
    code = current.get("weathercode", -1)

    weather_map = {
        0: ("☀️", "Clear"),
        1: ("🌤", "Mostly clear"),
        2: ("⛅", "Partly cloudy"),
        3: ("☁️", "Overcast"),
        45: ("🌫", "Fog"),
        48: ("🌫", "Rime fog"),
        51: ("🌦", "Light drizzle"),
        53: ("🌧", "Drizzle"),
        55: ("🌧", "Heavy drizzle"),
        61: ("🌦", "Light rain"),
        63: ("🌧", "Rain"),
        65: ("🌧", "Heavy rain"),
        71: ("🌨", "Light snow"),
        73: ("🌨", "Snow"),
        75: ("❄️", "Heavy snow"),
        80: ("🌦", "Showers"),
        95: ("⛈", "Thunderstorm"),
    }

    emoji, desc = weather_map.get(code, ("❔", f"Code {code}"))
    return f"{emoji}  {temp}\u00B0F {desc}"


def parse_location(arg: str) -> Optional[tuple[float, float]]:
    try:
        lat = float(arg)
        lon = float(sys.argv[2])
        return lat, lon
    except (ValueError, IndexError):
        geo_url = f"https://geocoding-api.open-meteo.com/v1/search?name={arg}&count=1"
        try:
            geo_resp = requests.get(geo_url, timeout=10)
            geo_resp.raise_for_status()
            geo_data = geo_resp.json()
        except requests.RequestException as exc:
            print(f"Geocoding failed: {exc}", file=sys.stderr)
            return None

        if not geo_data.get("results"):
            print("Location not found.", file=sys.stderr)
            return None

        result = geo_data["results"][0]
        print(f"Coordinates for '{arg}': {result['latitude']},{result['longitude']}")
        return result["latitude"], result["longitude"]


def main() -> int:
    if len(sys.argv) == 3:
        try:
            lat = float(sys.argv[1])
            lon = float(sys.argv[2])
        except ValueError:
            print("Latitude/longitude must be numeric.", file=sys.stderr)
            return 1
    elif len(sys.argv) == 2:
        coords = parse_location(sys.argv[1])
        if coords is None:
            return 1
        lat, lon = coords
    else:
        print("Usage:")
        print("  python weather.py <latitude> <longitude>")
        print("  python weather.py <zip_or_city>")
        return 1

    weather_text = get_weather(lat, lon)
    if weather_text is None:
        return 1

    print(weather_text)
    send_to_traycer(weather_text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
