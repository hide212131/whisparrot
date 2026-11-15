//! S-01 Rust 比較用の最小コンソール認識。
//! usage: spike-s01-rust <model.bin> <audio.wav(16kHz mono PCM16)> [lang]
use std::time::Instant;
use whisper_rs::{FullParams, SamplingStrategy, WhisperContext, WhisperContextParameters};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 3 {
        eprintln!("usage: spike-s01-rust <model> <wav> [lang]");
        std::process::exit(2);
    }
    let lang = args.get(3).map(String::as_str).unwrap_or("en");

    let mut reader = hound::WavReader::open(&args[2]).expect("open wav");
    let spec = reader.spec();
    assert_eq!(spec.sample_rate, 16000, "expects 16 kHz");
    assert_eq!(spec.channels, 1, "expects mono");
    let samples: Vec<f32> = reader
        .samples::<i16>()
        .map(|s| s.expect("sample") as f32 / 32768.0)
        .collect();

    let t_load = Instant::now();
    let ctx = WhisperContext::new_with_params(&args[1], WhisperContextParameters::default())
        .expect("load model");
    println!("model_load_ms={}", t_load.elapsed().as_millis());

    let mut state = ctx.create_state().expect("create state");
    let mut params = FullParams::new(SamplingStrategy::Greedy { best_of: 1 });
    params.set_language(Some(lang));
    params.set_print_progress(false);
    params.set_print_realtime(false);
    params.set_print_special(false);
    params.set_print_timestamps(false);

    let t = Instant::now();
    state.full(params, &samples).expect("full");
    println!("recognize_ms={}", t.elapsed().as_millis());

    let mut text = String::new();
    for seg in state.as_iter() {
        text.push_str(&seg.to_string());
    }
    println!("text={}", text.trim());
    println!("arch={}", std::env::consts::ARCH);
}
