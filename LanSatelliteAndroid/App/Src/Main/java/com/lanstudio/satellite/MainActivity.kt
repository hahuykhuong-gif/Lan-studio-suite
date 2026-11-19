package com.lanstudio.satellite

import android.Manifest
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.ImageFormat
import android.graphics.Rect
import android.graphics.YuvImage
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaRecorder
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.google.android.material.materialswitch.MaterialSwitch
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class MainActivity : AppCompatActivity() {

    companion object {
        private const val DISCOVERY_PORT = 45888
        private const val FRAME_PORT = 45889
        private const val CONTROL_PORT = 45890
        private const val TALKBACK_PORT = 45891
        private const val LAN_AUDIO_PORT = 45892
        private const val MIN_FRAME_INTERVAL_MS = 90L // ~11fps ổn định, giảm nóng máy & tải LAN
    }

    private lateinit var cameraPreview: PreviewView
    private lateinit var tvTallyBadge: TextView
    private lateinit var etCamName: EditText
    private lateinit var etPairToken: EditText
    private lateinit var switchAudioOnly: MaterialSwitch
    private lateinit var btnToggleStream: Button
    private lateinit var tvStatus: TextView

    private val prefs by lazy { getSharedPreferences("lan_satellite_prefs", MODE_PRIVATE) }

    private val deviceId: String by lazy {
        prefs.getString("fixed_device_id", null) ?: run {
            val modelTag = Build.MODEL.replace(Regex("[^A-Za-z0-9]"), "").take(8).ifBlank { "DEV" }
            val created = "${modelTag}_${UUID.randomUUID().toString().take(4).uppercase()}"
            prefs.edit().putString("fixed_device_id", created).apply()
            created
        }
    }

    private val isStreaming = AtomicBoolean(false)
    private var masterIp: String? = null
    private var activeToken: String = ""
    private val networkExecutor = Executors.newSingleThreadExecutor()
    private val cameraExecutor = Executors.newSingleThreadExecutor()
    private var lastFrameSentMs = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        cameraPreview = findViewById(R.id.cameraPreview)
        tvTallyBadge = findViewById(R.id.tvTallyBadge)
        etCamName = findViewById(R.id.etCamName)
        etPairToken = findViewById(R.id.etPairToken)
        switchAudioOnly = findViewById(R.id.switchAudioOnly)
        btnToggleStream = findViewById(R.id.btnToggleStream)
        tvStatus = findViewById(R.id.tvStatus)

        etCamName.setText(prefs.getString("custom_cam_name", "Cam-${Build.MODEL}"))
        etPairToken.setText(prefs.getString("pair_token", ""))
        tvStatus.text = "Mã máy: $deviceId | Chờ kết nối LAN..."

        etCamName.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, after: Int) {}
            override fun afterTextChanged(s: Editable?) {
                prefs.edit().putString("custom_cam_name", s?.toString()?.trim() ?: "").apply()
            }
        })

        etPairToken.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, after: Int) {}
            override fun afterTextChanged(s: Editable?) {
                prefs.edit().putString("pair_token", s?.toString()?.trim() ?: "").apply()
            }
        })

        requestPermissionsIfNeeded()
        startLanListeners()

        btnToggleStream.setOnClickListener {
            if (!isStreaming.get()) {
                isStreaming.set(true)
                btnToggleStream.text = "DỪNG GỬI"
                if (switchAudioOnly.isChecked) {
                    startLanAudioNodeLoop()
                } else {
                    startCameraX()
                }
                startTalkbackPlayerLoop()
                tvStatus.text = "Mã: $deviceId | Đang phát LAN..."
            } else {
                isStreaming.set(false)
                btnToggleStream.text = "BẮT ĐẦU GỬI LAN"
                tvStatus.text = "Mã: $deviceId | Đã dừng phát."
            }
        }
    }

    private fun requestPermissionsIfNeeded() {
        val needed = arrayOf(
            Manifest.permission.CAMERA,
            Manifest.permission.RECORD_AUDIO
        ).filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (needed.isNotEmpty()) {
            ActivityCompat.requestPermissions(this, needed.toTypedArray(), 101)
        }
    }

    private fun startLanListeners() {
        Thread {
            try {
                val socket = DatagramSocket(DISCOVERY_PORT).apply { broadcast = true }
                val buf = ByteArray(2048)
                while (!isFinishing) {
                    val packet = DatagramPacket(buf, buf.size)
                    socket.receive(packet)
                    val msg = String(packet.data, 0, packet.length)
                    val json = JSONObject(msg)
                    if (json.optString("type") == "MASTER_ANNOUNCE") {
                        val masterToken = json.optString("token", "")
                        val userToken = etPairToken.text.toString().trim()
                        if (userToken.isNotEmpty() && userToken != masterToken) continue

                        activeToken = masterToken
                        masterIp = packet.address.hostAddress
                        sendSatelliteHello(socket, packet.address, masterToken)
                        runOnUiThread {
                            tvStatus.text = "ID:$deviceId | Master:$masterIp | Live=${isStreaming.get()}"
                        }
                    }
                }
                socket.close()
            } catch (_: Exception) { }
        }.start()

        Thread {
            try {
                val socket = DatagramSocket(CONTROL_PORT)
                val buf = ByteArray(2048)
                while (!isFinishing) {
                    val packet = DatagramPacket(buf, buf.size)
                    socket.receive(packet)
                    val json = JSONObject(String(packet.data, 0, packet.length))
                    val cmdToken = json.optString("token", "")
                    if (activeToken.isNotEmpty() && cmdToken != activeToken) continue

                    val tally = json.optString("tally", "OFF")
                    val talk = json.optBoolean("talkback", false)
                    runOnUiThread {
                        applyTallyUi(tally, talk)
                    }
                }
                socket.close()
            } catch (_: Exception) { }
        }.start()
    }

    private fun sendSatelliteHello(socket: DatagramSocket, masterAddr: InetAddress, token: String) {
        val batteryIntent = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val level = batteryIntent?.getIntExtra(BatteryManager.EXTRA_LEVEL, 100) ?: 100
        val tempTenths = batteryIntent?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 320) ?: 320
        val customName = etCamName.text.toString().trim().ifBlank { "Cam-${Build.MODEL}" }

        val hello = JSONObject().apply {
            put("type", "SATELLITE_HELLO")
            put("token", token)
            put("deviceId", deviceId)
            put("name", customName)
            put("battery", level)
            put("temp", tempTenths / 10.0)
            put("audioOnly", switchAudioOnly.isChecked)
        }.toString().toByteArray()

        socket.send(DatagramPacket(hello, hello.size, masterAddr, DISCOVERY_PORT))
    }

    private fun applyTallyUi(tally: String, talkback: Boolean) {
        when (tally) {
            "PGM" -> {
                tvTallyBadge.text = if (talkback) "LIVE ● THOẠI" else "LIVE ● LÊN SÓNG"
                tvTallyBadge.setBackgroundColor(Color.parseColor("#DC2626"))
            }
            "PVW" -> {
                tvTallyBadge.text = if (talkback) "DEMO ● THOẠI" else "DEMO (MÀN PHỤ)"
                tvTallyBadge.setBackgroundColor(Color.parseColor("#059669"))
            }
            else -> {
                tvTallyBadge.text = if (talkback) "IDLE ● THOẠI" else "IDLE"
                tvTallyBadge.setBackgroundColor(Color.parseColor("#334155"))
            }
        }
    }

    private fun startCameraX() {
        val providerFuture = ProcessCameraProvider.getInstance(this)
        providerFuture.addListener({
            val cameraProvider = providerFuture.get()
            val preview = Preview.Builder().build().also {
                it.setSurfaceProvider(cameraPreview.surfaceProvider)
            }

            val analysis = ImageAnalysis.Builder()
                .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                .build()

            analysis.setAnalyzer(cameraExecutor) { image ->
                if (isStreaming.get() && masterIp != null) {
                    val now = System.currentTimeMillis()
                    if (now - lastFrameSentMs >= MIN_FRAME_INTERVAL_MS) {
                        lastFrameSentMs = now
                        val jpeg = imageProxyToJpeg(image)
                        if (jpeg != null) {
                            sendFrameToMaster(jpeg)
                        }
                    }
                }
                image.close()
            }

            try {
                cameraProvider.unbindAll()
                cameraProvider.bindToLifecycle(this, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis)
            } catch (_: Exception) { }
        }, ContextCompat.getMainExecutor(this))
    }

    private fun imageProxyToJpeg(image: ImageProxy): ByteArray? {
        return try {
            val yBuffer = image.planes[0].buffer
            val uBuffer = image.planes[1].buffer
            val vBuffer = image.planes[2].buffer

            val ySize = yBuffer.remaining()
            val uSize = uBuffer.remaining()
            val vSize = vBuffer.remaining()

            val nv21 = ByteArray(ySize + uSize + vSize)
            yBuffer.get(nv21, 0, ySize)
            vBuffer.get(nv21, ySize, vSize)
            uBuffer.get(nv21, ySize + vSize, uSize)

            val yuvImage = YuvImage(nv21, ImageFormat.NV21, image.width, image.height, null)
            val out = ByteArrayOutputStream()
            yuvImage.compressToJpeg(Rect(0, 0, image.width, image.height), 55, out)
            val rawJpeg = out.toByteArray()

            // Giảm kích thước 480x270 để giữ máy mát & mượt trên LAN
            val bmp = BitmapFactory.decodeByteArray(rawJpeg, 0, rawJpeg.size)
            val scaled = Bitmap.createScaledBitmap(bmp, 480, 270, false)
            bmp.recycle()
            val scaledOut = ByteArrayOutputStream()
            scaled.compress(Bitmap.CompressFormat.JPEG, 60, scaledOut)
            scaled.recycle()
            scaledOut.toByteArray()
        } catch (_: Exception) {
            null
        }
    }

    private fun sendFrameToMaster(jpeg: ByteArray) {
        val targetIp = masterIp ?: return
        networkExecutor.execute {
            try {
                val idBytes = deviceId.toByteArray(Charsets.UTF_8)
                val header = ByteBuffer.allocate(4).putInt(idBytes.size).array()
                val payload = ByteArray(4 + idBytes.size + jpeg.size)
                System.arraycopy(header, 0, payload, 0, 4)
                System.arraycopy(idBytes, 0, payload, 4, idBytes.size)
                System.arraycopy(jpeg, 0, payload, 4 + idBytes.size, jpeg.size)

                DatagramSocket().use { sock ->
                    sock.send(DatagramPacket(payload, payload.size, InetAddress.getByName(targetIp), FRAME_PORT))
                }
            } catch (_: Exception) { }
        }
    }

    private fun startLanAudioNodeLoop() {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return
        Thread {
            try {
                val sampleRate = 48000
                val minBuf = AudioRecord.getMinBufferSize(
                    sampleRate,
                    AudioFormat.CHANNEL_IN_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT
                )
                val recorder = AudioRecord(
                    MediaRecorder.AudioSource.MIC,
                    sampleRate,
                    AudioFormat.CHANNEL_IN_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    minBuf.coerceAtLeast(4096)
                )
                val pcmBuf = ByteArray(3840)
                val idBytes = deviceId.toByteArray(Charsets.UTF_8)
                val idLenBytes = ByteBuffer.allocate(4).putInt(idBytes.size).array()

                recorder.startRecording()
                DatagramSocket().use { sock ->
                    while (isStreaming.get() && switchAudioOnly.isChecked) {
                        val read = recorder.read(pcmBuf, 0, pcmBuf.size)
                        val ip = masterIp
                        if (read > 0 && ip != null) {
                            val pkt = ByteArray(4 + idBytes.size + read)
                            System.arraycopy(idLenBytes, 0, pkt, 0, 4)
                            System.arraycopy(idBytes, 0, pkt, 4, idBytes.size)
                            System.arraycopy(pcmBuf, 0, pkt, 4 + idBytes.size, read)
                            sock.send(DatagramPacket(pkt, pkt.size, InetAddress.getByName(ip), LAN_AUDIO_PORT))
                        }
                    }
                }
                recorder.stop()
                recorder.release()
            } catch (_: Exception) { }
        }.start()
    }

    private fun startTalkbackPlayerLoop() {
        Thread {
            try {
                val sampleRate = 48000
                val minBuf = AudioTrack.getMinBufferSize(
                    sampleRate,
                    AudioFormat.CHANNEL_OUT_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT
                )
                val track = AudioTrack(
                    AudioManager.STREAM_VOICE_CALL,
                    sampleRate,
                    AudioFormat.CHANNEL_OUT_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    minBuf.coerceAtLeast(4096),
                    AudioTrack.MODE_STREAM
                )
                track.play()

                val sock = DatagramSocket(TALKBACK_PORT)
                val buf = ByteArray(8192)
                while (isStreaming.get()) {
                    val pkt = DatagramPacket(buf, buf.size)
                    sock.receive(pkt)
                    track.write(pkt.data, 0, pkt.length)
                }
                sock.close()
                track.stop()
                track.release()
            } catch (_: Exception) { }
        }.start()
    }

    override fun onDestroy() {
        isStreaming.set(false)
        cameraExecutor.shutdown()
        networkExecutor.shutdown()
        super.onDestroy()
    }
}
