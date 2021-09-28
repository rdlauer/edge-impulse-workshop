#!/usr/bin/python3
from periphery import I2C
import notecard
import pyaudio

####################################
# UNCOMMENT TO CARD.RESTORE NOTECARD
####################################

# print("Connecting to Notecard...")
# port = I2C("/dev/i2c-1")
# card = notecard.OpenI2C(port, 0, 0, debug=False)

# req = {"req": "card.restore"}
# req["delete"] = True

# rsp = card.Transaction(req)
# print(rsp)

################################
# UNCOMMENT TO FIND MIC INPUT ID
################################

p = pyaudio.PyAudio()

for i in range(p.get_device_count()):
    print(p.get_device_info_by_index(i))
