#!/usr/bin/python3
import time
from periphery import I2C
import notecard
import base64
import keys
#import hashlib
import os
import shutil
import zipfile
import subprocess

productUID = keys.NOTEHUB_PRODUCT_UID
notification_file = "model-update.qi"

print("*************")
print("Edge Impulse ML Model Download")
print("*************")
print("Connecting to Notecard...")
port = I2C("/dev/i2c-1")
card = notecard.OpenI2C(port, 0, 0, debug=False)


def main():
    print(f'Associate Notecard with Notehub Project: {productUID}...')

    req = {"req": "hub.set"}
    req["product"] = productUID
    req["mode"] = "continuous"
    req["sync"] = True

    card.Transaction(req)


def get_updated_file_info():

    req = {"req": "hub.sync"}
    rsp = card.Transaction(req)

    time.sleep(2)

    req = {"req": "note.get"}
    req["file"] = notification_file
    req["delete"] = True

    rsp = card.Transaction(req)

    print("Checking Notehub for inbound note!")
    print(rsp)

    if "err" in rsp:
        return None
    else:
        return rsp["body"]


def get_sync_status():
    req = {"req": "hub.sync.status"}
    rsp = card.Transaction(req)

    print("Checking Notehub sync status...")
    print(rsp)

    if "err" in rsp:
        return "{error}"
    elif "status" in rsp:
        return rsp["status"]
    else:
        return ""


def web_get_chunk(file_to_update, chunk):
    req = {"req": "web.get"}
    req["route"] = "DownloadFileChunkRoute"
    req["name"] = f"/GetModelFileChunk?chunk={chunk}&filename={file_to_update}"

    try:
        rsp = card.Transaction(req)
        # print(rsp)
        return rsp["body"]["payload"]
        #md5_checksum = rsp["body"]["md5_checksum"]

        # compare md5 checksums, if invalid, request again
        # if hashlib.md5(payload).hexdigest() == md5_checksum:
        #     return payload
        # else:
        #     print("Checksum error downloading chunk. Retrying in 2 secs...")
        #     time.sleep(2)
        #     return web_get_chunk(file_to_update, chunk)
    except Exception as e:
        print("Other problem downloading chunk. Retrying in 2 secs...")
        print(e)
        time.sleep(2)
        return web_get_chunk(file_to_update, chunk)


def get_file_from_remote(file_to_update, total_chunks):
    print("Requesting chunk 1...")
    payload = web_get_chunk(file_to_update, 1)

    if total_chunks == 1:
        return payload
    else:
        print("File requires multiple requests...")
        file_string = payload

        for i in range(2, total_chunks + 1):
            print(f"Requesting chunk {i} of {total_chunks}")
            payload = web_get_chunk(file_to_update, i)
            file_string += payload

        return file_string


def save_file_and_restart(file_contents, file_name):
    # do some housecleaning
    if os.path.isdir("./edgeimpulse/build/temp"):
        shutil.rmtree("./edgeimpulse/build/temp")

    os.mkdir("./edgeimpulse/build/temp")

    # decode the base64-encoded file into zip archive
    contents_bytes = base64.b64decode(file_contents)

    with open(f'./edgeimpulse/build/temp/{file_name}.zip', 'wb') as result:
        result.write(contents_bytes)

    # extract zip archive
    with zipfile.ZipFile(f"./edgeimpulse/build/temp/{file_name}.zip", 'r') as zip_ref:
        zip_ref.extractall("./edgeimpulse/build/temp/model")

    # delete the existing "model-parameters" and "tflite-model" dirs
    shutil.rmtree("./edgeimpulse/model-parameters")
    shutil.rmtree("./edgeimpulse/tflite-model")

    # copy those same directories from the build/temp/model dir
    shutil.copytree("./edgeimpulse/build/temp/model/model-parameters",
                    "./edgeimpulse/model-parameters")
    shutil.copytree("./edgeimpulse/build/temp/model/tflite-model",
                    "./edgeimpulse/tflite-model")

    # build the .eim file
    cmd = 'cd edgeimpulse && APP_EIM=1 TARGET_LINUX_ARMV7=1 USE_FULL_TFLITE=1 make -j'
    subprocess.call(cmd, shell=True)

    # copy the .eim file from the build directory
    shutil.copyfile("./edgeimpulse/build/model.eim", "./model.eim")


main()

while True:

    file_to_update = None
    file_contents = None

    file_info = get_updated_file_info()
    total_chunks = 0

    if file_info is not None:
        file_to_update = file_info["filename"]
        total_chunks = file_info["total_chunks"]

    if file_to_update:
        print(
            f"Source file '{file_to_update}' has been updated remotely. Downloading...")

        # Make sure we aren't in the middle of a sync
        status = get_sync_status()
        while status != "" and "{error}" not in status and "{sync-end}" not in status and "{modem-off}" not in status:
            print("Waiting for sync to complete before continuing...")
            time.sleep(5)
            status = get_sync_status()

        file_contents = get_file_from_remote(file_to_update, total_chunks)

        if file_contents:
            save_file_and_restart(file_contents, file_to_update)
            pass
        else:
            print('Unable to update file...')

    print("Done...waiting 30 secs to try again")
    time.sleep(30)
