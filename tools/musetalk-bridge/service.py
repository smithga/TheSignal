"""
MuseTalk bridge service for TheSignal.Local.Server.

This is NOT a standalone project - copy this file into the root of a working
MuseTalk checkout (https://github.com/TMElyralab/MuseTalk) that already has its
models downloaded per the MuseTalk README (v1.5 weights under ./models/musetalkV15,
plus sd-vae and whisper), then run it from there:

    cd /path/to/MuseTalk
    cp /path/to/TheSignal/tools/musetalk-bridge/service.py .
    pip install fastapi "uvicorn[standard]" python-multipart
    python service.py

It exposes a small HTTP API that TheSignal.Local.Server's MuseTalkClient calls:
    POST /avatars/{avatar_id}/prepare  (multipart "photo")  -> one-time face preparation
    POST /avatars/{avatar_id}/generate (multipart "audio")  -> returns a lip-synced MP4

The Avatar class below is adapted from MuseTalk's scripts/realtime_inference.py
(same repo, Apache-2.0-compatible usage) so it can reuse the project's own
preprocessing/inference utilities instead of reimplementing them.
"""

import os
import sys
import glob
import json
import pickle
import shutil
import threading
import queue
import time
import copy
import tempfile

import numpy as np
import cv2
import torch
from fastapi import FastAPI, UploadFile, File, Form, HTTPException
from fastapi.responses import Response
from transformers import WhisperModel

from musetalk.utils.face_parsing import FaceParsing
from musetalk.utils.utils import datagen, load_all_model
from musetalk.utils.preprocessing import get_landmark_and_bbox, read_imgs
from musetalk.utils.blending import get_image_prepare_material, get_image_blending
from musetalk.utils.audio_processor import AudioProcessor


class _Args:
    """Mirrors the CLI defaults realtime_inference.py uses for MuseTalk v1.5."""
    version = "v15"
    gpu_id = 0
    vae_type = "sd-vae"
    unet_config = "./models/musetalkV15/musetalk.json"
    unet_model_path = "./models/musetalkV15/unet.pth"
    whisper_dir = "./models/whisper"
    extra_margin = 10
    parsing_mode = "jaw"
    left_cheek_width = 90
    right_cheek_width = 90
    audio_padding_length_left = 2
    audio_padding_length_right = 2
    batch_size = 8


args = _Args()
device = torch.device(f"cuda:{args.gpu_id}" if torch.cuda.is_available() else "cpu")
if device.type != "cuda":
    print("WARNING: no CUDA device found. MuseTalk requires an NVIDIA GPU; this will not work on CPU.")

vae, unet, pe = load_all_model(
    unet_model_path=args.unet_model_path,
    vae_type=args.vae_type,
    unet_config=args.unet_config,
    device=device,
)
timesteps = torch.tensor([0], device=device)
pe = pe.half().to(device)
vae.vae = vae.vae.half().to(device)
unet.model = unet.model.half().to(device)
weight_dtype = unet.model.dtype

audio_processor = AudioProcessor(feature_extractor_path=args.whisper_dir)
whisper = WhisperModel.from_pretrained(args.whisper_dir).to(device=device, dtype=weight_dtype).eval()
whisper.requires_grad_(False)

fp = FaceParsing(left_cheek_width=args.left_cheek_width, right_cheek_width=args.right_cheek_width)


class Avatar:
    """Adapted from MuseTalk's scripts/realtime_inference.py Avatar class."""

    def __init__(self, avatar_id, image_path):
        self.avatar_id = avatar_id
        self.image_path = image_path
        self.base_path = f"./results/v15/avatars/{avatar_id}"
        self.avatar_path = self.base_path
        self.full_imgs_path = f"{self.avatar_path}/full_imgs"
        self.coords_path = f"{self.avatar_path}/coords.pkl"
        self.latents_out_path = f"{self.avatar_path}/latents.pt"
        self.video_out_path = f"{self.avatar_path}/vid_output/"
        self.mask_out_path = f"{self.avatar_path}/mask"
        self.mask_coords_path = f"{self.avatar_path}/mask_coords.pkl"
        self.batch_size = args.batch_size
        self.idx = 0

        if os.path.exists(self.avatar_path):
            shutil.rmtree(self.avatar_path)
        for path in [self.avatar_path, self.full_imgs_path, self.video_out_path, self.mask_out_path]:
            os.makedirs(path, exist_ok=True)
        self.prepare_material()

    def prepare_material(self):
        shutil.copyfile(self.image_path, f"{self.full_imgs_path}/00000000.png")
        input_img_list = sorted(glob.glob(os.path.join(self.full_imgs_path, "*.[jpJP][pnPN]*[gG]")))

        coord_list, frame_list = get_landmark_and_bbox(input_img_list, 0)
        input_latent_list = []
        coord_placeholder = (0.0, 0.0, 0.0, 0.0)
        idx = -1
        for bbox, frame in zip(coord_list, frame_list):
            idx += 1
            if bbox == coord_placeholder:
                continue
            x1, y1, x2, y2 = bbox
            y2 = min(y2 + args.extra_margin, frame.shape[0])
            coord_list[idx] = [x1, y1, x2, y2]
            crop_frame = frame[y1:y2, x1:x2]
            resized_crop_frame = cv2.resize(crop_frame, (256, 256), interpolation=cv2.INTER_LANCZOS4)
            input_latent_list.append(vae.get_latents_for_unet(resized_crop_frame))

        if not input_latent_list:
            raise ValueError("No face detected in the supplied photo.")

        self.frame_list_cycle = frame_list + frame_list[::-1]
        self.coord_list_cycle = coord_list + coord_list[::-1]
        self.input_latent_list_cycle = input_latent_list + input_latent_list[::-1]
        self.mask_coords_list_cycle = []
        self.mask_list_cycle = []

        for i, frame in enumerate(self.frame_list_cycle):
            cv2.imwrite(f"{self.full_imgs_path}/{str(i).zfill(8)}.png", frame)
            x1, y1, x2, y2 = self.coord_list_cycle[i]
            mask, crop_box = get_image_prepare_material(frame, [x1, y1, x2, y2], fp=fp, mode=args.parsing_mode)
            cv2.imwrite(f"{self.mask_out_path}/{str(i).zfill(8)}.png", mask)
            self.mask_coords_list_cycle.append(crop_box)
            self.mask_list_cycle.append(mask)

        with open(self.mask_coords_path, "wb") as f:
            pickle.dump(self.mask_coords_list_cycle, f)
        with open(self.coords_path, "wb") as f:
            pickle.dump(self.coord_list_cycle, f)
        torch.save(self.input_latent_list_cycle, self.latents_out_path)

    def process_frames(self, res_frame_queue, video_len):
        while True:
            if self.idx >= video_len - 1:
                break
            try:
                res_frame = res_frame_queue.get(block=True, timeout=1)
            except queue.Empty:
                continue
            bbox = self.coord_list_cycle[self.idx % len(self.coord_list_cycle)]
            ori_frame = copy.deepcopy(self.frame_list_cycle[self.idx % len(self.frame_list_cycle)])
            x1, y1, x2, y2 = bbox
            try:
                res_frame = cv2.resize(res_frame.astype(np.uint8), (x2 - x1, y2 - y1))
            except Exception:
                self.idx += 1
                continue
            mask = self.mask_list_cycle[self.idx % len(self.mask_list_cycle)]
            mask_crop_box = self.mask_coords_list_cycle[self.idx % len(self.mask_coords_list_cycle)]
            combine_frame = get_image_blending(ori_frame, res_frame, bbox, mask, mask_crop_box)
            cv2.imwrite(f"{self.avatar_path}/tmp/{str(self.idx).zfill(8)}.png", combine_frame)
            self.idx += 1

    @torch.no_grad()
    def inference(self, audio_path, out_vid_name, fps):
        os.makedirs(self.avatar_path + "/tmp", exist_ok=True)
        whisper_input_features, librosa_length = audio_processor.get_audio_feature(audio_path, weight_dtype=weight_dtype)
        whisper_chunks = audio_processor.get_whisper_chunk(
            whisper_input_features,
            device,
            weight_dtype,
            whisper,
            librosa_length,
            fps=fps,
            audio_padding_length_left=args.audio_padding_length_left,
            audio_padding_length_right=args.audio_padding_length_right,
        )

        video_num = len(whisper_chunks)
        res_frame_queue = queue.Queue()
        self.idx = 0
        process_thread = threading.Thread(target=self.process_frames, args=(res_frame_queue, video_num))
        process_thread.start()

        gen = datagen(whisper_chunks, self.input_latent_list_cycle, self.batch_size)
        for whisper_batch, latent_batch in gen:
            audio_feature_batch = pe(whisper_batch.to(device))
            latent_batch = latent_batch.to(device=device, dtype=unet.model.dtype)
            pred_latents = unet.model(latent_batch, timesteps, encoder_hidden_states=audio_feature_batch).sample
            pred_latents = pred_latents.to(device=device, dtype=vae.vae.dtype)
            for res_frame in vae.decode_latents(pred_latents):
                res_frame_queue.put(res_frame)
        process_thread.join()

        output_vid = os.path.join(self.video_out_path, out_vid_name + ".mp4")
        temp_vid = f"{self.avatar_path}/temp.mp4"
        os.system(f"ffmpeg -y -v warning -r {fps} -f image2 -i {self.avatar_path}/tmp/%08d.png -vcodec libx264 -vf format=yuv420p -crf 18 {temp_vid}")
        os.system(f"ffmpeg -y -v warning -i {audio_path} -i {temp_vid} {output_vid}")
        os.remove(temp_vid)
        shutil.rmtree(f"{self.avatar_path}/tmp")
        return output_vid


app = FastAPI()
_avatars: dict[str, Avatar] = {}


@app.post("/avatars/{avatar_id}/prepare")
async def prepare_avatar(avatar_id: str, photo: UploadFile = File(...)):
    suffix = os.path.splitext(photo.filename or "photo.png")[1] or ".png"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        tmp.write(await photo.read())
        tmp_path = tmp.name
    try:
        _avatars[avatar_id] = Avatar(avatar_id, tmp_path)
    except Exception as ex:
        raise HTTPException(status_code=502, detail=str(ex)) from ex
    finally:
        os.remove(tmp_path)
    return {"status": "prepared", "avatar_id": avatar_id}


@app.post("/avatars/{avatar_id}/generate")
async def generate_video(avatar_id: str, audio: UploadFile = File(...), fps: int = Form(25)):
    avatar = _avatars.get(avatar_id)
    if avatar is None:
        raise HTTPException(status_code=404, detail=f"Avatar '{avatar_id}' has not been prepared yet. Call /avatars/{avatar_id}/prepare first.")

    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
        tmp.write(await audio.read())
        tmp_path = tmp.name

    try:
        out_name = f"line-{int(time.time() * 1000)}"
        video_path = avatar.inference(tmp_path, out_name, fps)
        with open(video_path, "rb") as f:
            video_bytes = f.read()
        os.remove(video_path)
        return Response(content=video_bytes, media_type="video/mp4")
    except Exception as ex:
        raise HTTPException(status_code=502, detail=str(ex)) from ex
    finally:
        os.remove(tmp_path)


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=8001)
