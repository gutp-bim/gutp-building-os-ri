import asyncio
import json
import argparse
import sys

from azure.eventhub import EventData
from azure.eventhub.aio import EventHubProducerClient

EVENT_HUB_CONNECTION_STR = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
VALID_TELEMETRY_TYPES = ["electric", "environmental", "hvac", "behavior", "bacnet"]

filePaths = {
    "electric": "../DotNet/BuildingOS.Functions.Test/TestData/electric-device-message.json",
    "environmental": "../DotNet/BuildingOS.Functions.Test/TestData/environmental-device-message.json",
    "hvac": "../DotNet/BuildingOS.Functions.Test/TestData/hvac-device-message.json",
    "behavior": "../DotNet/BuildingOS.Functions.Test/TestData/behavior-sensor-message.json",
    "bacnet": "../DotNet/BuildingOS.Functions.Test/TestData/bacnet-device-message.json"
}
hubNames = {
    "electric": "electricdata",
    "environmental": "environmentaldata",
    "hvac": "hvacdata",
    "behavior": "behaviordata",
    "bacnet": "bacnetdata"
}

def main():
    parser = argparse.ArgumentParser(
        description="Choose one or more telemetry types."
    )
    parser.add_argument("telemetryTypes", nargs="+", help="Choose one or more telemetry types.", choices=VALID_TELEMETRY_TYPES)
    args = parser.parse_args()
    invalid_telemetry_types = [tel_type for tel_type in args.telemetryTypes if tel_type not in VALID_TELEMETRY_TYPES]

    if invalid_telemetry_types:
        print(f"Invalid telemetry types: {', '.join(invalid_telemetry_types)}")
        print(f"Valid telemetry types are: {', '.join(VALID_TELEMETRY_TYPES)}")
        sys.exit(1)
    
    for type in args.telemetryTypes:
        asyncio.run(send(filePaths[type], hubNames[type]))
        print(f"Sent {type} data.")
    return
                
async def send(filePath, hubName):
    with open(filePath, "r", encoding="utf-8_sig") as json_file:
        data = json.load(json_file)

    producer = EventHubProducerClient.from_connection_string(
        conn_str=EVENT_HUB_CONNECTION_STR, eventhub_name=hubName
    )
    async with producer:
        event_data_batch = await producer.create_batch()
        json_string = json.dumps(data, indent=4)
        event_data = EventData(json_string)
        event_data_batch.add(event_data)

        await producer.send_batch(event_data_batch)

if __name__ == "__main__":
    main()
