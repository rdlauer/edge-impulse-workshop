#!/usr/bin/python3
import notecard
from notecard import hub
from periphery import I2C
from scipy.io import wavfile
import base64
import math
import uuid
import keys

API_KEY = keys.EDGE_IMPULSE_API_KEY
CHUNK_SIZE = 8000  # size of file chunks in bytes
WAV_FILE = "sample_final.wav"

# get the sample rate from the wav file
samplerate, wav_data = wavfile.read(WAV_FILE)

# load the wav file into a byte array
wav = open(WAV_FILE, "rb").read()

# base64-encode the wav file
wav_encoded = base64.b64encode(open(WAV_FILE, "rb").read())
wav_encoded = str(wav_encoded, "ascii", "ignore")

# determine the number of chunks we need to send to notehub.io
note_number = int(math.ceil(len(wav_encoded)/CHUNK_SIZE))

# create a guid to uniquely identify the chunks on the server
guid = str(uuid.uuid4())

# init the notecard
productUID = keys.NOTEHUB_PRODUCT_UID
port = I2C("/dev/i2c-1")
nCard = notecard.OpenI2C(port, 0, 0)

# connect notecard to notehub
rsp = hub.set(nCard, product=productUID, mode="continuous", sync=True)
print(rsp)

# validate notecard is connected to notehub before proceeding
hub_connected = False

while not hub_connected:
    req = {"req": "hub.status"}
    rsp = nCard.Transaction(req)
    if rsp["connected"] == True:
        hub_connected = True

# create list of failed chunks for retries if needed
error_list = []


def send_chunk(n):
    req = {"req": "web.post"}
    req["route"] = "PostAudioFileRoute"
    req["name"] = f"/PostAudioFile?guid={guid}&note_number={note_number}&note_index={n + 1}&samplerate={1000/samplerate}"
    req["payload"] = wav_encoded[n * CHUNK_SIZE:n * CHUNK_SIZE + CHUNK_SIZE]

    rsp = nCard.Transaction(req)

    print("HTTP POSTing: *" + str(n + 1) + "* " + str(rsp))

    return rsp


for n in range(note_number):
    rsp = send_chunk(n)
    if "err" in rsp:
        error_list.append(n)

while error_list:
    for n in error_list:
        rsp = send_chunk(n)
        if "payload" in rsp:
            error_list.remove(n)


print("All done! Time to check Edge Impulse Studio!")
